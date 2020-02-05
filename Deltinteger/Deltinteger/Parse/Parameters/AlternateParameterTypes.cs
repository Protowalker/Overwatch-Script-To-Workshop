using System;
using System.IO;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.LanguageServer;

namespace Deltin.Deltinteger.Parse
{
    /// <summary>
    /// `VariableParameter` takes indexed variables as parameters.
    /// </summary>
    class VariableParameter : CodeParameter
    {
        private VariableType VariableType { get; }
        private VariableResolveOptions Options { get; }

        public VariableParameter(string name, string documentation, VariableResolveOptions options = null) : base(name, documentation)
        {
            VariableType = VariableType.Dynamic;
            Options = options ?? new VariableResolveOptions();
        }
        public VariableParameter(string name, string documentation, VariableType variableType, VariableResolveOptions options = null) : base(name, documentation)
        {
            if (variableType == VariableType.ElementReference) throw new Exception("Only the variable types Dynamic, Global, and Player is valid.");
            VariableType = variableType;
            Options = options ?? new VariableResolveOptions();
        }

        public override object Validate(ScriptFile script, IExpression value, DocRange valueRange)
        {
            VariableResolve resolvedVariable = new VariableResolve(Options, value, valueRange, script.Diagnostics);

            // Syntax error if the expression is not a variable.
            if (!resolvedVariable.DoesResolveToVariable)
                script.Diagnostics.Error("Expected a variable.", valueRange);
                        
            else if (VariableType != VariableType.Dynamic && resolvedVariable.SetVariable.Calling.VariableType != VariableType)
            {
                if (VariableType == VariableType.Global)
                    script.Diagnostics.Error($"Expected a global variable.", valueRange);
                else
                    script.Diagnostics.Error($"Expected a player variable.", valueRange);
            }
            
            else return resolvedVariable;
            return null;
        }

        public override IWorkshopTree Parse(ActionSet actionSet, IExpression expression, object additionalParameterData, bool asElement)
        {
            VariableResolve resolvedVariable = (VariableResolve)additionalParameterData;
            return ((IndexReference)actionSet.IndexAssigner[resolvedVariable.SetVariable.Calling]).WorkshopVariable;
        }
    }

    class ConstBoolParameter : CodeParameter
    {
        private bool DefaultConstValue { get; }

        public ConstBoolParameter(string name, string documentation) : base(name, documentation) {}
        public ConstBoolParameter(string name, string documentation, bool defaultValue)
            : base(name, documentation, new ExpressionOrWorkshopValue(defaultValue ? (Element)new V_True() : new V_False()))
        {
            DefaultConstValue = defaultValue;
        }

        public override object Validate(ScriptFile script, IExpression value, DocRange valueRange)
        {
            if (value is ExpressionOrWorkshopValue)
                return ((ExpressionOrWorkshopValue)value).WorkshopValue is V_True;

            if (value is BoolAction == false)
            {
                script.Diagnostics.Error("Expected a boolean constant.", valueRange);
                return null;
            }

            return ((BoolAction)value).Value;
        }
    }

    class ConstNumberParameter : CodeParameter
    {
        private double DefaultConstValue { get; }

        public ConstNumberParameter(string name, string documentation) : base(name, documentation) {}
        public ConstNumberParameter(string name, string documentation, double defaultValue) : base(name, documentation, new ExpressionOrWorkshopValue(new V_Number(defaultValue)))
        {
            DefaultConstValue = defaultValue;
        }

        public override object Validate(ScriptFile script, IExpression value, DocRange valueRange)
        {
            if (value == null) return DefaultConstValue;

            if (value is NumberAction == false)
            {
                script.Diagnostics.Error("Expected a number constant.", valueRange);
                return null;
            }

            return ((NumberAction)value).Value;
        }
    }

    class ConstStringParameter : CodeParameter
    {
        public ConstStringParameter(string name, string documentation) : base(name, documentation) {}

        public override object Validate(ScriptFile script, IExpression value, DocRange valueRange)
        {
            StringAction str = value as StringAction;
            if (str == null) script.Diagnostics.Error("Expected string constant.", valueRange);
            return str?.Value;
        }

        public override IWorkshopTree Parse(ActionSet actionSet, IExpression expression, object additionalParameterData, bool asElement) => null;
    }

    class FileParameter : CodeParameter
    {
        public string FileType { get; }

        /// <summary>
        /// A parameter that resolves to a file. AdditionalParameterData will return the file path.
        /// </summary>
        /// <param name="fileType">The expected file type. Can be null.</param>
        /// <param name="parameterName">The name of the parameter.</param>
        /// <param name="description">The parameter's description. Can be null.</param>
        public FileParameter(string fileType, string parameterName, string description) : base(parameterName, null, null, description)
        {
            FileType = fileType?.ToLower();
        }

        public override object Validate(ScriptFile script, IExpression value, DocRange valueRange)
        {
            StringAction str = value as StringAction;
            if (str == null)
            {
                script.Diagnostics.Error("Expected string constant.", valueRange);
                return null;
            }

            string resultingPath = Extras.CombinePathWithDotNotation(script.Uri.FilePath(), str.Value);
            
            if (resultingPath == null)
            {
                script.Diagnostics.Error("File path contains invalid characters.", valueRange);
                return null;
            }

            string dir = Path.GetDirectoryName(resultingPath);
            if (Directory.Exists(dir))
                DeltinScript.AddImportCompletion(script, dir, valueRange);

            if (!File.Exists(resultingPath))
            {
                script.Diagnostics.Error($"No file was found at '{resultingPath}'.", valueRange);
                return null;
            }

            if (FileType != null && Path.GetExtension(resultingPath).ToLower() != "." + FileType)
            {
                script.Diagnostics.Error($"Expected a file with the file type '{FileType}'.", valueRange);
                return null;
            }

            script.AddDefinitionLink(valueRange, new Location(Extras.Definition(resultingPath), DocRange.Zero));
            script.AddHover(valueRange, resultingPath);

            return resultingPath;
        }
    }
}