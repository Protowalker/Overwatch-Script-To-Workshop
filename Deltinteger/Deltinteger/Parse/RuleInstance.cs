using Deltin.Deltinteger;
using Deltin.Deltinteger.LanguageServer;
using Deltin.Deltinteger.Parse;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Deltin.Parse
{
    public class RuleInstance
    {
        public string BaseRuleName { get; }
        public string Name { get; }

        public List<IExpression> ParameterValues { get; private set; } = new List<IExpression>();

        public RuleAction rule;

        ParseInfo scriptInfo;
        DocRange range;

        public RuleInstance(ParseInfo scriptInfo, Scope scope, Deltinteger.DeltinScriptParser.Rule_instanceContext instanceContext)
        {
            BaseRuleName = instanceContext.PART().GetText();
            if (instanceContext.STRINGLITERAL() != null) Name = Extras.RemoveQuotes(instanceContext.STRINGLITERAL().GetText());
            else scriptInfo.Script.Diagnostics.Error("Rule must have a name", DocRange.GetRange(instanceContext.RULE_WORD()));
            this.scriptInfo = scriptInfo;

            range = DocRange.GetRange(instanceContext);

            var parameters = instanceContext.call_parameters();

            if (parameters == null) scriptInfo.Script.Diagnostics.Error("No parameters found", DocRange.GetRange(instanceContext.name));
            else {
                foreach (var param in parameters.expr())
                {
                    ParameterValues.Add(scriptInfo.GetExpression(scope, param));
                }
            }

        }

        public void FindRule(List<RuleAction> rules)
        {
            rule = rules.Where(r => r.IsRuleMacro && r.Name == BaseRuleName).FirstOrDefault();
            if (rule == null) scriptInfo.Script.Diagnostics.Error($"Base rule {BaseRuleName} does not exist.", range);
            if (ParameterValues.Count < rule.Parameters.Length) scriptInfo.Script.Diagnostics.Error($"Not enough arguments", range);
            else if (ParameterValues.Count > rule.Parameters.Length) scriptInfo.Script.Diagnostics.Error($"Too many arguments", range);

        }

        private IWorkshopTree[] GetParameterValuesAsWorkshop(ActionSet actionSet)
        {
            if (ParameterValues == null) return new IWorkshopTree[0];

            IWorkshopTree[] parameterValues = new IWorkshopTree[ParameterValues.Count];
            for (int i = 0; i < ParameterValues.Count; i++)
                parameterValues[i] = ParameterValues[i].Parse(actionSet);
            return parameterValues;
        }
    }
}
