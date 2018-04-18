// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Xunit;


namespace Microsoft.AspNetCore.Blazor.Razor
{
    public class ComponentDocumentRewritePassTest
    {
        public ComponentDocumentRewritePassTest()
        {
            var test = TagHelperDescriptorBuilder.Create("test", "test");
            test.TagMatchingRule(b => b.TagName = "test");

            TagHelpers = new List<TagHelperDescriptor>()
            {
                test.Build(),
            };

            Pass = new ComponentDocumentRewritePass();
            Engine = RazorProjectEngine.Create(
                BlazorExtensionInitializer.DefaultConfiguration,
                RazorProjectFileSystem.Create(Environment.CurrentDirectory), 
                b =>
                {
                    b.Features.Add(new ComponentDocumentClassifierPass());
                    b.Features.Add(Pass);
                    b.Features.Add(new StaticTagHelperFeature() { TagHelpers = TagHelpers, });
                }).Engine;
        }

        private RazorEngine Engine { get; }

        private ComponentDocumentRewritePass Pass { get; }

        private List<TagHelperDescriptor> TagHelpers { get; }

        [Fact]
        public void Execute_RewritesHtml_Basic()
        {
            // Arrange
            var document = CreateDocument(@"
<html>
  <head cool=""beans"">
    Hello, World!
  </head>
</html>");

            var documentNode = Lower(document);

            // Act
            Pass.Execute(document, documentNode);

            // Assert
            var method = documentNode.FindPrimaryMethod();

            var html = NodeAssert.Element(method.Children[1], "html");
            Assert.Equal("html", html.TagName);
            Assert.Collection(
                html.Children,
                c => NodeAssert.Whitespace(c),
                c => NodeAssert.Element(c, "head"),
                c => NodeAssert.Whitespace(c));

            var head = NodeAssert.Element(html.Children[1], "head");
            Assert.Collection(
                head.Children,
                c => NodeAssert.Attribute(c, "cool", "beans"),
                c => NodeAssert.Content(c, "Hello, World!"));
        }

        [Fact]
        public void Execute_RewritesHtml_Mixed()
        {
            // Arrange
            var document = CreateDocument(@"
<html>
  <head cool=""beans"" csharp=""@yes"" mixed=""hi @there"">
  </head>
</html>");

            var documentNode = Lower(document);

            // Act
            Pass.Execute(document, documentNode);

            // Assert
            var method = documentNode.FindPrimaryMethod();

            var html = NodeAssert.Element(method.Children[1], "html");
            Assert.Equal("html", html.TagName);
            Assert.Collection(
                html.Children,
                c => NodeAssert.Whitespace(c),
                c => NodeAssert.Element(c, "head"),
                c => NodeAssert.Whitespace(c));

            var head = NodeAssert.Element(html.Children[1], "head");
            Assert.Collection(
                head.Children,
                c => NodeAssert.Attribute(c, "cool", "beans"),
                c => NodeAssert.CSharpAttribute(c, "csharp", "yes"),
                c => Assert.IsType<HtmlAttributeIntermediateNode>(c),
                c => NodeAssert.Whitespace(c));

            var mixed = Assert.IsType<HtmlAttributeIntermediateNode>(head.Children[2]);
            Assert.Collection(
                mixed.Children,
                c => Assert.IsType<HtmlAttributeValueIntermediateNode>(c),
                c => Assert.IsType<CSharpExpressionAttributeValueIntermediateNode>(c));
        }

        [Fact]
        public void Execute_RewritesHtml_WithCode()
        {
            // Arrange
            var document = CreateDocument(@"
<html>
  @if (some_bool)
  {
  <head cool=""beans"">
    @hello
  </head>
  }
</html>");

            var documentNode = Lower(document);

            // Act
            Pass.Execute(document, documentNode);

            // Assert
            var method = documentNode.FindPrimaryMethod();

            var html = NodeAssert.Element(method.Children[1], "html");
            Assert.Equal("html", html.TagName);
            Assert.Collection(
                html.Children,
                c => NodeAssert.Whitespace(c),
                c => Assert.IsType<CSharpCodeIntermediateNode>(c),
                c => Assert.IsType<CSharpCodeIntermediateNode>(c),
                c => NodeAssert.Whitespace(c),
                c => NodeAssert.Element(c, "head"),
                c => NodeAssert.Whitespace(c),
                c => Assert.IsType<CSharpCodeIntermediateNode>(c));

            var head = NodeAssert.Element(html.Children[4], "head");
            Assert.Collection(
                head.Children,
                c => NodeAssert.Attribute(c, "cool", "beans"),
                c => NodeAssert.Whitespace(c),
                c => Assert.IsType<CSharpExpressionIntermediateNode>(c),
                c => NodeAssert.Whitespace(c));
        }

        [Fact]
        public void Execute_RewritesHtml_TagHelper()
        {
            // Arrange
            var document = CreateDocument(@"
@addTagHelper ""*, test""
<html>
  <test>
    <head cool=""beans"">
      Hello, World!
    </head>
  </test>
</html>");

            var documentNode = Lower(document);

            // Act
            Pass.Execute(document, documentNode);

            // Assert
            var method = documentNode.FindPrimaryMethod();

            var html = NodeAssert.Element(method.Children[2], "html");
            Assert.Equal("html", html.TagName);
            Assert.Collection(
                html.Children,
                c => NodeAssert.Whitespace(c),
                c => Assert.IsType<TagHelperIntermediateNode>(c),
                c => NodeAssert.Whitespace(c));

            var body = html.Children
                .OfType<TagHelperIntermediateNode>().Single().Children
                .OfType<TagHelperBodyIntermediateNode>().Single();

            Assert.Collection(
                body.Children,
                c => NodeAssert.Whitespace(c),
                c => NodeAssert.Element(c, "head"),
                c => NodeAssert.Whitespace(c));

            var head = body.Children[1];
            Assert.Collection(
                head.Children,
                c => NodeAssert.Attribute(c, "cool", "beans"),
                c => NodeAssert.Content(c, "Hello, World!"));
        }

        private RazorCodeDocument CreateDocument(string content)
        {
            var source = RazorSourceDocument.Create(content, "test.cshtml");
            return RazorCodeDocument.Create(source);
        }

        private DocumentIntermediateNode Lower(RazorCodeDocument codeDocument)
        {
            for (var i = 0; i < Engine.Phases.Count; i++)
            {
                var phase = Engine.Phases[i];
                if (phase is IRazorDocumentClassifierPhase)
                {
                    break;
                }

                phase.Execute(codeDocument);
            }

            var document = codeDocument.GetDocumentIntermediateNode();
            Engine.Features.OfType<ComponentDocumentClassifierPass>().Single().Execute(codeDocument, document);
            return document;
        }

        private class StaticTagHelperFeature : ITagHelperFeature
        {
            public RazorEngine Engine { get; set; }

            public List<TagHelperDescriptor> TagHelpers { get; set; }

            public IReadOnlyList<TagHelperDescriptor> GetDescriptors()
            {
                return TagHelpers;
            }
        }
    }
}