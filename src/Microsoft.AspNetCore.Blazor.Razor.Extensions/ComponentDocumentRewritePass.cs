// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AngleSharp;
using AngleSharp.Html;
using AngleSharp.Parser.Html;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Blazor.Razor
{
    // Rewrites the standard IR to a format more suitable for Blazor
    //
    // HTML nodes are rewritten to contain more structure, instead of treating HTML as opaque content
    // it is structured into element/component nodes, and attribute nodes.
    internal class ComponentDocumentRewritePass : IntermediateNodePassBase, IRazorDocumentClassifierPass
    {
        // Per the HTML spec, the following elements are inherently self-closing
        // For example, <img> is the same as <img /> (and therefore it cannot contain descendants)
        private readonly static HashSet<string> VoidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
        };

        // Run as soon as possible after the Component document classifier
        public override int Order => ComponentDocumentClassifierPass.DefaultFeatureOrder + 1;

        protected override void ExecuteCore(RazorCodeDocument codeDocument, DocumentIntermediateNode documentNode)
        {
            if (documentNode.DocumentKind != ComponentDocumentClassifierPass.ComponentDocumentKind)
            {
                return;
            }

            var visitor = new RewriteWalker();
            visitor.Visit(documentNode);
        }

        // Visits nodes then rewrites them using a post-order traversal. The result is that the tree
        // is rewritten bottom up.
        //
        // This relies on a few invariants Razor already provides for correctness.
        // - Tag Helpers are the only real nesting construct
        // - Tag Helpers require properly nested HTML inside their body
        //
        // This means that when we find a 'container' for HTML content, we have the guarantee
        // that the content is properly nested, except at the top level of scope. And since the top
        // level isn't nested inside anything, we can't introduce any errors due to misunderstanding
        // the structure.
        private class RewriteWalker : IntermediateNodeWalker
        {
            public override void VisitDefault(IntermediateNode node)
            {
                var foundHtml = false;
                for (var i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    Visit(child);

                    if (child is HtmlContentIntermediateNode)
                    {
                        foundHtml = true;
                    }
                }

                if (foundHtml)
                {
                    RewriteChildren(node);
                }
            }

            private void RewriteChildren(IntermediateNode node)
            {
                // We expect all of the immediate children of a node (together) to comprise
                // a well-formed tree of elements and components. 
                var stack = new Stack<IntermediateNode>();
                stack.Push(node);

                // Make a copy, we will clear and rebuild the child collection of this node.
                var children = node.Children.ToArray();
                node.Children.Clear();

                // Due to the way Anglesharp parses HTML (tags at a time) we need to keep track of some state.
                // This handles cases like:
                //
                //  <foo bar="17" baz="@baz" />
                //
                // This will lower like:
                //
                //  HtmlContent <foo bar="17"
                //  HtmlAttribute baz=" - "
                //      CSharpAttributeValue baz
                //  HtmlContent  />
                //
                // We need to consume HTML until we see the 'end tag' for <foo /> and then we can 
                // the attributes from the parsed HTML and the CSharpAttribute value.
                var unconsumedHtml = string.Empty;
                var attributes = new List<HtmlAttributeIntermediateNode>();

                for (var i = 0; i < children.Length; i++)
                {
                    if (children[i] is HtmlContentIntermediateNode htmlNode)
                    {
                        var content = GetHtmlContent(htmlNode);
                        if (unconsumedHtml != null)
                        {
                            // If we have unparsed HTML from a previous node, try and complete parsing it now.
                            content = unconsumedHtml + content;
                            unconsumedHtml = null;
                        }

                        var tokenizer = new HtmlTokenizer(new TextSource(content), HtmlEntityService.Resolver);

                        HtmlToken token;
                        while ((token = tokenizer.Get()).Type != HtmlTokenType.EndOfFile)
                        {
                            switch (token.Type)
                            {
                                case HtmlTokenType.Character:
                                    {
                                        // Text content

                                        // Ignore whitespace if we're not inside a tag.
                                        if (stack.Peek() is MethodDeclarationIntermediateNode && string.IsNullOrWhiteSpace(token.Data))
                                        {
                                            break;
                                        }

                                        stack.Peek().Children.Add(new HtmlContentIntermediateNode()
                                        {
                                            Children =
                                            {
                                                new IntermediateToken() { Content = token.Data, Kind = TokenKind.Html, }
                                            }
                                        });
                                        break;
                                    }

                                case HtmlTokenType.StartTag:
                                case HtmlTokenType.EndTag:
                                    {
                                        var tag = token.AsTag();

                                        if (token.Type == HtmlTokenType.StartTag)
                                        {
                                            var elementNode = new HtmlElementIntermediateNode()
                                            {
                                                TagName = GetTagNameWithOriginalCase(content, tag),
                                            };

                                            stack.Peek().Children.Add(elementNode);
                                            stack.Push(elementNode);

                                            for (var j = 0; j < tag.Attributes.Count; j++)
                                            {
                                                var attribute = tag.Attributes[j];
                                                stack.Peek().Children.Add(CreateAttributeNode(attribute));
                                            }

                                            for (var j = 0; j < attributes.Count; j++)
                                            {
                                                stack.Peek().Children.Add(attributes[j]);
                                            }
                                            attributes.Clear();
                                        }

                                        if (token.Type == HtmlTokenType.EndTag || tag.IsSelfClosing || VoidElements.Contains(tag.Data))
                                        {
                                            var popped = (HtmlElementIntermediateNode)stack.Pop();
                                            if (!string.Equals(popped.TagName, tag.Data, StringComparison.OrdinalIgnoreCase))
                                            {
                                                var diagnostic = BlazorDiagnosticFactory.Create_MismatchedClosingTag(null, popped.TagName, token.Data);
                                                popped.Diagnostics.Add(diagnostic);
                                            }
                                        }

                                        break;
                                    }

                                case HtmlTokenType.Comment:
                                    break;

                                default:
                                    throw new InvalidCastException($"Unsupported token type: {token.Type.ToString()}");
                            }
                        }

                        // If we got an EOF in the middle of an HTML element, it's probably because we're
                        // about to receive some attribute name/value pairs. Store the unused HTML content
                        // so we can prepend it to the part that comes after the attributes to make
                        // complete valid markup.
                        if (content.Length > token.Position.Position)
                        {
                            unconsumedHtml = content.Substring(token.Position.Position - 1);
                        }
                    }
                    else if (children[i] is HtmlAttributeIntermediateNode htmlAttribute)
                    {
                        // Buffer the attribute for now, it will get written out as part of a tag.
                        attributes.Add(htmlAttribute);
                    }
                    else
                    {
                        // not HTML, or already rewritten.
                        stack.Peek().Children.Add(children[i]);
                    }
                }

                if (stack.Peek() != node)
                {
                    // not balanced
                    throw null;
                }

                if (unconsumedHtml != null)
                {
                    // extra HTML
                    throw null;
                }
            }
        }

        private static HtmlAttributeIntermediateNode CreateAttributeNode(KeyValuePair<string, string> attribute)
        {
            return new HtmlAttributeIntermediateNode()
            {
                AttributeName = attribute.Key,
                Children =
                {
                    new HtmlAttributeValueIntermediateNode()
                    {
                        Children =
                        {
                            new IntermediateToken()
                            {
                                Kind = TokenKind.Html,
                                Content = attribute.Value,
                            },
                        }
                    },
                }
            };
        }

        private static string GetHtmlContent(HtmlContentIntermediateNode node)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < node.Children.Count; i++)
            {
                var token = node.Children[i] as IntermediateToken;
                if (token != null && token.IsHtml)
                {
                    builder.Append(token.Content);
                }
            }

            return builder.ToString();
        }

        // Anglesharp canonicalizes the case of tags, we want what the user typed.
        private static string GetTagNameWithOriginalCase(string document, HtmlTagToken tagToken)
        {
            var offset = tagToken.Type == HtmlTokenType.EndTag ? 1 : 0; // For end tags, skip the '/'
            return document.Substring(tagToken.Position.Position + offset, tagToken.Name.Length);
        }
    }
}