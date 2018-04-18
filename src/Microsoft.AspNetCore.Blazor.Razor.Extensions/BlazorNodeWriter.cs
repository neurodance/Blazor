﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language.CodeGeneration;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Blazor.Razor
{
    internal abstract class BlazorNodeWriter : IntermediateNodeWriter
    {
        public abstract void BeginWriteAttribute(CodeWriter codeWriter, string key);

        public abstract void WriteComponentOpen(CodeRenderingContext context, ComponentOpenExtensionNode node);

        public abstract void WriteComponentClose(CodeRenderingContext context, ComponentCloseExtensionNode node);

        public abstract void WriteComponentBody(CodeRenderingContext context, ComponentBodyExtensionNode node);

        public abstract void WriteComponentAttribute(CodeRenderingContext context, ComponentAttributeExtensionNode node);

        public abstract void WriteHtmlElement(CodeRenderingContext context, HtmlElementIntermediateNode node);

        public sealed override void BeginWriterScope(CodeRenderingContext context, string writer)
        {
            throw new NotImplementedException(nameof(BeginWriterScope));
        }

        public sealed override void EndWriterScope(CodeRenderingContext context)
        {
            throw new NotImplementedException(nameof(EndWriterScope));
        }

        public sealed override void WriteCSharpCodeAttributeValue(CodeRenderingContext context, CSharpCodeAttributeValueIntermediateNode node)
        {
            // We used to support syntaxes like <elem onsomeevent=@{ /* some C# code */ } /> but this is no longer the 
            // case.
            //
            // We provide an error for this case just to be friendly.
            var content = string.Join("", node.Children.OfType<IntermediateToken>().Select(t => t.Content));
            context.Diagnostics.Add(BlazorDiagnosticFactory.Create_CodeBlockInAttribute(node.Source, content));
            return;
        }
    }
}
