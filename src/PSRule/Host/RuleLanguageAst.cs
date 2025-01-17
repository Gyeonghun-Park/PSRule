﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Threading;
using PSRule.Pipeline;
using PSRule.Resources;

namespace PSRule.Host
{
    internal sealed class RuleLanguageAst : AstVisitor
    {
        private const string PARAMETER_NAME = "Name";
        private const string PARAMETER_BODY = "Body";
        private const string PARAMETER_ERRORACTION = "ErrorAction";
        private const string RULE_KEYWORD = "Rule";
        private const string ERRORID_PARAMETERNOTFOUND = "PSRule.Parse.RuleParameterNotFound";
        private const string ERRORID_INVALIDRULENESTING = "PSRule.Parse.InvalidRuleNesting";
        private const string ERRORID_INVALIDERRORACTION = "PSRule.Parse.InvalidErrorAction";

        private readonly StringComparer _Comparer;

        internal List<ErrorRecord> Errors;

        internal RuleLanguageAst(PipelineContext context)
        {
            _Comparer = StringComparer.OrdinalIgnoreCase;
        }

        private sealed class ParameterBindResult
        {
            public ParameterBindResult()
            {
                Bound = new Dictionary<string, CommandElementAst>(StringComparer.OrdinalIgnoreCase);
                Unbound = new List<CommandElementAst>();
                _Offset = 0;
            }

            public Dictionary<string, CommandElementAst> Bound;
            public List<CommandElementAst> Unbound;

            private int _Offset;

            public bool Has<TAst>(string parameterName, out TAst parameterValue) where TAst : CommandElementAst
            {
                var result = Bound.TryGetValue(parameterName, out CommandElementAst value) && value is TAst;
                parameterValue = result ? value as TAst : null;
                return result;
            }

            public bool Has<TAst>(string parameterName, int position, out TAst value) where TAst : CommandElementAst
            {
                // Try bound
                if (Has<TAst>(parameterName, out value))
                {
                    _Offset++;
                    return true;
                }
                int relative = position - _Offset;
                var result = Unbound.Count > relative && Unbound[relative] is TAst;
                value = result ? Unbound[relative] as TAst : null;
                return result;
            }
        }

        public override AstVisitAction VisitCommand(CommandAst commandAst)
        {
            if (IsRule(commandAst))
            {
                var valid = NotNested(commandAst) &&
                    HasValidErrorAction(commandAst) &&
                    HasRequiredParameters(commandAst);

                return valid ? base.VisitCommand(commandAst) : AstVisitAction.SkipChildren;
            }
            return base.VisitCommand(commandAst);
        }

        /// <summary>
        /// Determines if the rule has a Body parameter.
        /// </summary>
        private bool HasBodyParameter(CommandAst commandAst, ParameterBindResult bindResult)
        {
            if (bindResult.Has(PARAMETER_BODY, 1, out ScriptBlockExpressionAst _))
                return true;

            ReportError(ERRORID_PARAMETERNOTFOUND, PSRuleResources.RuleParameterNotFound, PARAMETER_BODY, ReportExtent(commandAst.Extent));
            return false;
        }

        /// <summary>
        /// Determines if the rule has a Name parameter.
        /// </summary>
        private bool HasNameParameter(CommandAst commandAst, ParameterBindResult bindResult)
        {
            if (bindResult.Has(PARAMETER_NAME, 0, out StringConstantExpressionAst value) && !string.IsNullOrEmpty(value.Value))
                return true;

            ReportError(ERRORID_PARAMETERNOTFOUND, PSRuleResources.RuleParameterNotFound, PARAMETER_NAME, ReportExtent(commandAst.Extent));
            return false;
        }

        /// <summary>
        /// Determines if the rule is nested in another rule.
        /// </summary>
        private bool NotNested(CommandAst commandAst)
        {
            if (GetParentBlock(commandAst)?.Parent == null)
                return true;

            ReportError(ERRORID_INVALIDRULENESTING, PSRuleResources.InvalidRuleNesting, ReportExtent(commandAst.Extent));
            return false;
        }

        /// <summary>
        /// Determines if the rule has required parameters.
        /// </summary>
        private bool HasRequiredParameters(CommandAst commandAst)
        {
            var bindResult = BindParameters(commandAst);
            return HasNameParameter(commandAst, bindResult) && HasBodyParameter(commandAst, bindResult);
        }

        /// <summary>
        /// Determine if the rule has allowed ErrorAction options.
        /// </summary>
        private bool HasValidErrorAction(CommandAst commandAst)
        {
            var bindResult = BindParameters(commandAst);
            if (!bindResult.Has(PARAMETER_ERRORACTION, 0, out StringConstantExpressionAst value))
                return true;

            if (!Enum.TryParse(value.Value, out ActionPreference result) || (result == ActionPreference.Ignore || result == ActionPreference.Stop))
                return true;

            ReportError(ERRORID_INVALIDERRORACTION, PSRuleResources.InvalidErrorAction, value.Value, ReportExtent(commandAst.Extent));
            return false;
        }

        /// <summary>
        /// Determines if the command is a rule definition.
        /// </summary>
        private bool IsRule(CommandAst commandAst)
        {
            return _Comparer.Equals(commandAst.GetCommandName(), RULE_KEYWORD);
        }

        private static ParameterBindResult BindParameters(CommandAst commandAst)
        {
            var result = new ParameterBindResult();
            int i = 1;
            int next = 2;
            for (; i < commandAst.CommandElements.Count; i++, next++)
            {
                // Is named parameter
                if (commandAst.CommandElements[i] is CommandParameterAst parameter && next < commandAst.CommandElements.Count)
                {
                    result.Bound.Add(parameter.ParameterName, commandAst.CommandElements[next]);
                    i++;
                    next++;
                }
                else
                {
                    result.Unbound.Add(commandAst.CommandElements[i]);
                }
            }
            return result;
        }

        private void ReportError(string errorId, string message, params object[] args)
        {
            ReportError(new Pipeline.ParseException(
                message: string.Format(Thread.CurrentThread.CurrentCulture, message, args),
                errorId: errorId
            ));
        }

        private void ReportError(Pipeline.ParseException exception)
        {
            if (Errors == null)
            {
                Errors = new List<ErrorRecord>();
            }

            Errors.Add(new ErrorRecord(
                exception: exception,
                errorId: exception.ErrorId,
                errorCategory: ErrorCategory.InvalidOperation,
                targetObject: null
            ));
        }

        private static string ReportExtent(IScriptExtent extent)
        {
            return string.Concat(extent.File, " line ", extent.StartLineNumber);
        }

        private static ScriptBlockAst GetParentBlock(Ast ast)
        {
            var block = ast;
            while (block != null && !(block is ScriptBlockAst))
                block = block.Parent;

            return (ScriptBlockAst)block;
        }
    }
}
