// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Blazor.Razor
{
    // We use some tag helpers that can be applied directly to HTML elements. When
    // that happens, the default lowering pass will map the whole element as a tag helper.
    //
    // This phase exists to turn these 'orphan' tag helpers back into HTML elements so that
    // go down the proper path for rendering.
    internal class OrphanTagHelperLoweringPass : IntermediateNodePassBase, IRazorOptimizationPass
    {
        // Run after our other passes
        public override int Order => 1000;

        protected override void ExecuteCore(RazorCodeDocument codeDocument, DocumentIntermediateNode documentNode)
        {
            if (codeDocument == null)
            {
                throw new ArgumentNullException(nameof(codeDocument));
            }

            if (documentNode == null)
            {
                throw new ArgumentNullException(nameof(documentNode));
            }

            var visitor = new Visitor();
            visitor.Visit(documentNode);

            for (var i = 0; i < visitor.References.Count; i++)
            {
                var reference = visitor.References[i];
                var tagHelperNode = (TagHelperIntermediateNode)reference.Node;

                // Since this is converted from a tag helper to a regular old HTML element, we need to 
                // flatten out the structure
                var element = new HtmlElementIntermediateNode()
                {
                    TagName = tagHelperNode.TagName,
                };

                for (var j = 0; j < tagHelperNode.Diagnostics.Count; j++)
                {
                    element.Diagnostics.Add(tagHelperNode.Diagnostics[j]);
                }

                // We expect to see a body node, followed by a series of property/attribute nodes
                // This isn't really the order we want, so skip over the body for now, and we'll do another
                // pass that merges it in.
                TagHelperBodyIntermediateNode body = null;
                for (var j = 0; j < tagHelperNode.Children.Count; j++)
                {
                    if (tagHelperNode.Children[j] is TagHelperBodyIntermediateNode)
                    {
                        body = (TagHelperBodyIntermediateNode)tagHelperNode.Children[j];
                        continue;
                    }
                    else if (tagHelperNode.Children[j] is TagHelperHtmlAttributeIntermediateNode htmlAttribute)
                    {
                        element.Children.Add(RewriteTagHelperHtmlAttribute(htmlAttribute));
                    }
                    else
                    {
                        // We shouldn't see anything else here, but just in case, add the content as-is.
                        element.Children.Add(tagHelperNode.Children[j]);
                    }
                }

                for (var j = 0; j < body.Children.Count; j++)
                {
                    element.Children.Add(body.Children[j]);
                }

                reference.InsertAfter(element);
                reference.Remove();
            }
        }

        private IntermediateNode RewriteTagHelperHtmlAttribute(TagHelperHtmlAttributeIntermediateNode attribute)
        {
            var node = new HtmlAttributeIntermediateNode()
            {
                AttributeName = attribute.AttributeName,
                Source = attribute.Source,
            };

            for (var i = 0; i < attribute.Children.Count; i++)
            {
                node.Children.Add(attribute.Children[i]);
            }

            for (var i = 0; i < attribute.Diagnostics.Count; i++)
            {
                node.Diagnostics.Add(attribute.Diagnostics[i]);
            }

            return node;
        }

        private class Visitor : IntermediateNodeWalker
        {
            public List<IntermediateNodeReference> References = new List<IntermediateNodeReference>();

            public override void VisitTagHelper(TagHelperIntermediateNode node)
            {
                base.VisitTagHelper(node);

                // Use a post-order traversal because we're going to rewrite tag helper nodes, and thus
                // change the parent nodes.
                //
                // This ensures that we operate the leaf nodes first.
                if (!node.TagHelpers.Any(t => t.IsComponentTagHelper()))
                {
                    References.Add(new IntermediateNodeReference(Parent, node));
                }
            }
        }
    }
}
