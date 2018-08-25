// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Blazor.Razor
{
    internal class ComponentLoweringPass : IntermediateNodePassBase, IRazorOptimizationPass
    {
        // This pass runs earlier than our other passes that 'lower' specific kinds of attributes.
        public override int Order => 0;

        protected override void ExecuteCore(RazorCodeDocument codeDocument, DocumentIntermediateNode documentNode)
        {
            var @namespace = documentNode.FindPrimaryNamespace();
            var @class = documentNode.FindPrimaryClass();
            if (@namespace == null || @class == null)
            {
                // Nothing to do, bail. We can't function without the standard structure.
                return;
            }

            // For each component *usage* we need to rewrite the tag helper node to map to the relevant component
            // APIs.
            var references = documentNode.FindDescendantReferences<TagHelperIntermediateNode>();
            for (var i = 0; i < references.Count; i++)
            {
                var reference = references[i];
                var node = (TagHelperIntermediateNode)reference.Node;

                var count = 0;
                for (var j = 0; j < node.TagHelpers.Count; j++)
                {
                    if (node.TagHelpers[j].IsComponentTagHelper())
                    {
                        // Only allow a single component tag helper per element. If there are multiple, we'll just consider
                        // the first one and ignore the others.
                        if (count++ > 1)
                        {
                            node.Diagnostics.Add(BlazorDiagnosticFactory.Create_MultipleComponents(node.Source, node.TagName, node.TagHelpers));
                            break;
                        }
                    }
                }

                if (count >= 1)
                {
                    reference.Replace(RewriteAsComponent(node, node.TagHelpers.First(t => t.IsComponentTagHelper())));
                }
                else
                {
                    reference.Replace(RewriteAsElement(node));
                }
            }
        }

        private ComponentExtensionNode RewriteAsComponent(TagHelperIntermediateNode node, TagHelperDescriptor tagHelper)
        {
            var result = new ComponentExtensionNode()
            {
                Component = tagHelper,
                Source = node.Source,
                TagName = node.TagName,
            };

            for (var i = 0; i < node.Diagnostics.Count; i++)
            {
                result.Diagnostics.Add(node.Diagnostics[i]);
            }

            var visitor = new ComponentRewriteVisitor(result.Children);
            visitor.Visit(node);

            return result;
        }

        private HtmlElementIntermediateNode RewriteAsElement(TagHelperIntermediateNode node)
        {
            var result = new HtmlElementIntermediateNode()
            {
                Source = node.Source,
                TagName = node.TagName,
            };

            for (var i = 0; i < node.Diagnostics.Count; i++)
            {
                result.Diagnostics.Add(node.Diagnostics[i]);
            }

            var visitor = new ElementRewriteVisitor(result.Children);
            visitor.Visit(node);

            return result;
        }

        private class ComponentRewriteVisitor : IntermediateNodeWalker
        {
            private readonly IntermediateNodeCollection _children;

            public ComponentRewriteVisitor(IntermediateNodeCollection children)
            {
                _children = children;
            }

            public override void VisitTagHelper(TagHelperIntermediateNode node)
            {
                // Visit children, we're replacing this node.
                for (var i = 0; i < node.Children.Count; i++)
                {
                    Visit(node.Children[i]);
                }
            }

            public override void VisitTagHelperBody(TagHelperBodyIntermediateNode node)
            {
                for (var i = 0; i < node.Children.Count; i++)
                {
                    _children.Add(node.Children[i]);
                }
            }

            public override void VisitTagHelperHtmlAttribute(TagHelperHtmlAttributeIntermediateNode node)
            {
                var attribute = new ComponentAttributeExtensionNode(node);
                _children.Add(attribute);

                // Since we don't support complex content, we can rewrite the inside of this
                // node to the rather simpler form that property nodes usually have.
                for (var i = 0; i < attribute.Children.Count; i++)
                {
                    IntermediateNode child = null;
                    if (attribute.Children[i] is HtmlAttributeValueIntermediateNode htmlValue)
                    {
                        child = new HtmlContentIntermediateNode()
                        {
                            Source = htmlValue.Source,
                        };

                        for (var j = 0; j < htmlValue.Children.Count; j++)
                        {
                            child.Children.Add(htmlValue.Children[j]);
                        }
                    }
                    else if (
                        attribute.Children[i] is CSharpExpressionAttributeValueIntermediateNode &&
                        attribute.Children[i].Children.Count > 0 &&
                        attribute.Children[i].Children[0] is TemplateIntermediateNode template)
                    {
                        // We recognize templates as special form since we want to treat them differently than an expression.
                        // Removing the expression from the tree makes this easier to recognize later.
                        attribute.Children[i] = template;
                    }
                    else if (attribute.Children[i] is CSharpExpressionAttributeValueIntermediateNode expressionValue)
                    {
                        child = new CSharpExpressionIntermediateNode()
                        {
                            Source = expressionValue.Source,
                        };

                        // As a special case here, remove any empty tokens. Templates in component attributes
                        // get lowered with a trailing empty token, and it's will cause problems down the line.
                        for (var j = 0; j < expressionValue.Children.Count; j++)
                        {
                            if (expressionValue.Children[j] is IntermediateToken token && string.IsNullOrEmpty(token.Content))
                            {
                                continue;
                            }

                            child.Children.Add(expressionValue.Children[j]);
                        }
                    }
                    else if (attribute.Children[i] is CSharpCodeAttributeValueIntermediateNode codeValue)
                    {
                        child = new CSharpExpressionIntermediateNode()
                        {
                            Source = codeValue.Source,
                        };

                        for (var j = 0; j < codeValue.Children.Count; j++)
                        {
                            child.Children.Add(codeValue.Children[j]);
                        }
                    }

                    if (child != null)
                    {
                        attribute.Children[i] = child;
                    }
                }
            }

            public override void VisitTagHelperProperty(TagHelperPropertyIntermediateNode node)
            {
                // Each 'tag helper property' belongs to a specific tag helper. We want to handle
                // the cases for components, but leave others alone. This allows our other passes
                // to handle those cases.
                var child = (IntermediateNode)node;
                if (node.TagHelper.IsComponentTagHelper())
                {
                    child = new ComponentAttributeExtensionNode(node);

                    // As a special case, simplify templates so they are easy to recognize later
                    if (child.Children.Count == 1 &&
                        child.Children[0] is CSharpExpressionIntermediateNode &&
                        child.Children[0].Children.Count == 1 &&
                        child.Children[0].Children[0] is TemplateIntermediateNode template)
                    {
                        child.Children[0] = template;
                    }
                }

                _children.Add(child);
            }

            public override void VisitDefault(IntermediateNode node)
            {
                _children.Add(node);
            }
        }

        private class ElementRewriteVisitor : IntermediateNodeWalker
        {
            private readonly IntermediateNodeCollection _children;

            public ElementRewriteVisitor(IntermediateNodeCollection children)
            {
                _children = children;
            }

            public override void VisitTagHelper(TagHelperIntermediateNode node)
            {
                // Visit children, we're replacing this node.
                for (var i = 0; i < node.Children.Count; i++)
                {
                    Visit(node.Children[i]);
                }
            }

            public override void VisitTagHelperBody(TagHelperBodyIntermediateNode node)
            {
                for (var i = 0; i < node.Children.Count; i++)
                {
                    _children.Add(node.Children[i]);
                }
            }

            public override void VisitTagHelperHtmlAttribute(TagHelperHtmlAttributeIntermediateNode node)
            {
                var attribute = new HtmlAttributeIntermediateNode()
                {
                    AttributeName = node.AttributeName,
                    Source = node.Source,
                };
                _children.Add(attribute);

                for (var i = 0; i < node.Diagnostics.Count; i++)
                {
                    attribute.Diagnostics.Add(node.Diagnostics[i]);
                }

                switch (node.AttributeStructure)
                {
                    case AttributeStructure.Minimized:

                        attribute.Prefix = node.AttributeName;
                        attribute.Suffix = string.Empty;
                        break;

                    case AttributeStructure.NoQuotes:
                    case AttributeStructure.SingleQuotes:
                    case AttributeStructure.DoubleQuotes:

                        // We're ignoring attribute structure here for simplicity, it doesn't effect us.
                        attribute.Prefix = node.AttributeName + "=\"";
                        attribute.Suffix = "\"";

                        for (var i = 0; i < node.Children.Count; i++)
                        {
                            attribute.Children.Add(RewriteAttributeContent(node.Children[i]));
                        }

                        break;
                }

                IntermediateNode RewriteAttributeContent(IntermediateNode content)
                {
                    if (content is HtmlContentIntermediateNode html)
                    {
                        var value = new HtmlAttributeValueIntermediateNode()
                        {
                            Source = content.Source,
                        };

                        for (var i = 0; i < html.Children.Count; i++)
                        {
                            value.Children.Add(html.Children[i]);
                        }

                        for (var i = 0; i < html.Diagnostics.Count; i++)
                        {
                            value.Diagnostics.Add(html.Diagnostics[i]);
                        }

                        return value;
                    }


                    return content;
                }
            }

            public override void VisitTagHelperProperty(TagHelperPropertyIntermediateNode node)
            {
                // Each 'tag helper property' belongs to a specific tag helper. We want to handle
                // the cases for components, but leave others alone. This allows our other passes
                // to handle those cases.
                _children.Add(node.TagHelper.IsComponentTagHelper() ? (IntermediateNode)new ComponentAttributeExtensionNode(node) : node);
            }

            public override void VisitDefault(IntermediateNode node)
            {
                _children.Add(node);
            }
        }
    }
}
