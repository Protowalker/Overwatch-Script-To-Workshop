﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using Antlr4;
using Antlr4.Runtime;
using Deltin.Deltinteger;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.LanguageServer;

namespace Deltin.Deltinteger.Parse
{
    public class Parser
    {
        static Log Log = new Log("Parse");

        public static Rule[] ParseText(string document, out ParserElements parserData)
        {
            parserData = ParserElements.GetParser(document, null);

            Log.Write(LogLevel.Normal, new ColorMod("Build succeeded.", ConsoleColor.Green));

            // List all variables
            Log.Write(LogLevel.Normal, new ColorMod("Variable Guide:", ConsoleColor.Blue));

            if (parserData.Root?.VarCollection().Count > 0)
            {
                int nameLength = parserData.Root.VarCollection().Max(v => v.Name.Length);

                bool other = false;
                foreach (DefinedVar var in parserData.Root.VarCollection())
                {
                    ConsoleColor textcolor = other ? ConsoleColor.White : ConsoleColor.DarkGray;
                    other = !other;

                    Log.Write(LogLevel.Normal,
                        // Names
                        new ColorMod(var.Name + new string(' ', nameLength - var.Name.Length) + "  ", textcolor),
                        // Variable
                        new ColorMod(
                            (var.IsGlobal ? "global" : "player") 
                            + " " + 
                            var.Variable.ToString() +
                            (var.Index != -1 ? $"[{var.Index}]" : "")
                            , textcolor)
                    );
                }
            }

            return parserData.Rules;
        }
    }

    public class ParserElements
    {
        public static ParserElements GetParser(string document, Pos documentPos)
        {
            AntlrInputStream inputStream = new AntlrInputStream(document);

            // Lexer
            DeltinScriptLexer lexer = new DeltinScriptLexer(inputStream);
            CommonTokenStream commonTokenStream = new CommonTokenStream(lexer);

            // Parse
            DeltinScriptParser parser = new DeltinScriptParser(commonTokenStream);
            var errorListener = new ErrorListener();
            parser.RemoveErrorListeners();
            parser.AddErrorListener(errorListener);

            DeltinScriptParser.RulesetContext ruleSetContext = parser.ruleset();

            AdditionalErrorChecking aec = new AdditionalErrorChecking(parser, errorListener);
            aec.Visit(ruleSetContext);

            List<Diagnostic> diagnostics = new List<Diagnostic>();
            diagnostics.AddRange(errorListener.Errors);

            // Get the ruleset node.
            BuildAstVisitor bav = null;
            RulesetNode ruleSetNode = null;

            VarCollection vars = null;
            ScopeGroup root = null;
            List<UserMethod> userMethods = null;
            Rule[] rules = null;
            bool success = false;

            if (diagnostics.Count == 0)
            {
                vars = new VarCollection();
                root = new ScopeGroup();
                userMethods = new List<UserMethod>();

                bav = new BuildAstVisitor(documentPos);
                ruleSetNode = (RulesetNode)bav.Visit(ruleSetContext);

                foreach (var definedVar in ruleSetNode.DefinedVars)
                    vars.AssignDefinedVar(root, definedVar.IsGlobal, definedVar.VariableName, definedVar.Range);

                // Get the user methods.
                for (int i = 0; i < ruleSetNode.UserMethods.Length; i++)
                    userMethods.Add(new UserMethod(ruleSetNode.UserMethods[i]));

                // Parse the rules.
                rules = new Rule[ruleSetNode.Rules.Length];

                for (int i = 0; i < rules.Length; i++)
                {
                    try
                    {
                        var result = Translate.GetRule(ruleSetNode.Rules[i], root, vars, userMethods.ToArray());
                        rules[i] = result.Rule;
                        diagnostics.AddRange(result.Diagnostics);
                    }
                    catch (SyntaxErrorException ex)
                    {
                        diagnostics.Add(new Diagnostic(ex.Message, ex.Range) { severity = Diagnostic.Error });
                    }
                }

                success = true;
            }
            
            return new ParserElements()
            {
                Parser = parser,
                RulesetContext = ruleSetContext,
                RuleSetNode = ruleSetNode,
                Bav = bav,
                Diagnostics = diagnostics,
                Rules = rules,
                UserMethods = userMethods?.ToArray(),
                Root = root,
                Success = success
            };
        }

        public DeltinScriptParser Parser { get; private set; }
        public DeltinScriptParser.RulesetContext RulesetContext { get; private set; }
        public RulesetNode RuleSetNode { get; private set; }
        //public ErrorListener ErrorListener { get; private set; } 
        public List<Diagnostic> Diagnostics;
        public BuildAstVisitor Bav { get; private set; }
        public Rule[] Rules { get; private set; }
        public UserMethod[] UserMethods { get; private set; }
        public ScopeGroup Root { get; private set; }
        public bool Success { get; private set; }
    }

    class Translate
    {
        public static TranslateResult GetRule(RuleNode ruleNode, ScopeGroup root, VarCollection varCollection, UserMethod[] userMethods)
        {
            var result = new Translate(ruleNode, root, varCollection, userMethods);
            return new TranslateResult(result.Rule, result.Diagnostics.ToArray());
        }

        private readonly ScopeGroup Root;
        private readonly VarCollection VarCollection;
        private readonly UserMethod[] UserMethods;
        private readonly Rule Rule;
        private readonly List<Element> Actions = new List<Element>();
        private readonly List<Condition> Conditions = new List<Condition>();
        private readonly bool IsGlobal;
        private readonly List<A_Skip> ReturnSkips = new List<A_Skip>(); // Return statements whos skip count needs to be filled out.
        private readonly ContinueSkip ContinueSkip; // Contains data about the wait/skip for continuing loops.
        private readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        private readonly List<MethodStack> MethodStack = new List<MethodStack>(); // The user method stack

        private Translate(RuleNode ruleNode, ScopeGroup root, VarCollection varCollection, UserMethod[] userMethods)
        {
            Root = root;
            VarCollection = varCollection;
            UserMethods = userMethods;

            Rule = new Rule(ruleNode.Name);

            ContinueSkip = new ContinueSkip(IsGlobal, Actions, varCollection);

            ParseConditions(ruleNode.Conditions);
            ParseBlock(root.Child(), ruleNode.Block, false, varCollection.AssignVar(IsGlobal));

            Rule.Actions = Actions.ToArray();
            Rule.Conditions = Conditions.ToArray();

            // Fufill remaining skips
            foreach (var skip in ReturnSkips)
                skip.ParameterValues = new object[] { new V_Number(Actions.Count - ReturnSkips.IndexOf(skip)) };
            ReturnSkips.Clear();
        }

        void ParseConditions(IExpressionNode[] expressions)
        {
            foreach(var expr in expressions)
            {
                Element parsedIf = ParseExpression(Root, expr);
                // If the parsed if is a V_Compare, translate it to a condition.
                // Makes "(value1 == value2) == true" to just "value1 == value2"
                if (parsedIf is V_Compare)
                    Conditions.Add(
                        new Condition(
                            (Element)parsedIf.ParameterValues[0],
                            (Operators)parsedIf.ParameterValues[1],
                            (Element)parsedIf.ParameterValues[2]
                        )
                    );
                // If not, just do "parsedIf == true"
                else
                    Conditions.Add(new Condition(
                        parsedIf, Operators.Equal, new V_True()
                    ));
            }
        }

        Element ParseExpression(ScopeGroup scope, IExpressionNode expression)
        {
            switch (expression)
            {
                // Math and boolean operations.
                case OperationNode operationNode:
                {
                    Element left = ParseExpression(scope, operationNode.Left);
                    Element right = ParseExpression(scope, operationNode.Right);

                    if (Constants.BoolOperations.Contains(operationNode.Operation))
                    {
                        if (left.ElementData.ValueType != Elements.ValueType.Any && left.ElementData.ValueType != Elements.ValueType.Boolean)
                        {
                            throw new SyntaxErrorException($"Expected boolean, got {left .ElementData.ValueType.ToString()} instead.", ((Node)operationNode.Left).Range);
                            //Diagnostics.Add(new Diagnostic($"Expected boolean, got {left .ElementData.ValueType.ToString()} instead.", ((Node)operationNode.Left).Range));
                            //return null;
                        }
                        
                        if (right.ElementData.ValueType != Elements.ValueType.Any && right.ElementData.ValueType != Elements.ValueType.Boolean)
                        {
                            throw new SyntaxErrorException($"Expected boolean, got {right.ElementData.ValueType.ToString()} instead.", ((Node)operationNode.Right).Range);
                            //Diagnostics.Add(new Diagnostic($"Expected boolean, got {right.ElementData.ValueType.ToString()} instead.", ((Node)operationNode.Right).Range));
                            //return null;
                        }
                    }

                    switch (operationNode.Operation)
                    {
                        // Math: ^, *, %, /, +, -
                        case "^":
                            return Element.Part<V_RaiseToPower>(left, right);

                        case "*":
                            return Element.Part<V_Multiply>(left, right);

                        case "%":
                            return Element.Part<V_Modulo>(left, right);

                        case "/":
                            return Element.Part<V_Divide>(left, right);

                        case "+":
                            return Element.Part<V_Add>(left, right);

                        case "-":
                            return Element.Part<V_Subtract>(left, right);


                        // BoolCompare: &, |
                        case "&":
                            return Element.Part<V_And>(left, right);

                        case "|":
                            return Element.Part<V_Or>(left, right);

                        // Compare: <, <=, ==, >=, >, !=
                        case "<":
                            return Element.Part<V_Compare>(left, Operators.LessThan, right);

                        case "<=":
                            return Element.Part<V_Compare>(left, Operators.LessThanOrEqual, right);

                        case "==":
                            return Element.Part<V_Compare>(left, Operators.Equal, right);

                        case ">=":
                            return Element.Part<V_Compare>(left, Operators.GreaterThanOrEqual, right);

                        case ">":
                            return Element.Part<V_Compare>(left, Operators.GreaterThan, right);

                        case "!=":
                            return Element.Part<V_Compare>(left, Operators.NotEqual, right);
                    }
                    
                    throw new Exception($"Operation {operationNode.Operation} not implemented.");
                }

                // Number
                case NumberNode numberNode:
                    return new V_Number(numberNode.Value);
                
                // Bool
                case BooleanNode boolNode:
                    if (boolNode.Value)
                        return new V_True();
                    else
                        return new V_False();
                
                // Not operation
                case NotNode notNode:
                    return Element.Part<V_Not>(ParseExpression(scope, notNode.Value));

                // Strings
                case StringNode stringNode:
                    Element[] stringFormat = new Element[stringNode.Format?.Length ?? 0];
                    for (int i = 0; i < stringFormat.Length; i++)
                        stringFormat[i] = ParseExpression(scope, stringNode.Format[i]);
                    return V_String.ParseString(stringNode.Range, stringNode.Value, stringFormat);

                // Null
                case NullNode nullNode:
                    return new V_Null();

                // TODO check if groups need to be implemented here

                // Methods
                case MethodNode methodNode:
                    return ParseMethod(scope, methodNode, true);

                // Variable
                case VariableNode variableNode:
                    return scope.GetVar(variableNode.Name, variableNode.Range, Diagnostics)
                        .GetVariable(variableNode.Target != null ? ParseExpression(scope, variableNode.Target) : null);

                // Get value in array
                case ValueInArrayNode viaNode:
                    return Element.Part<V_ValueInArray>(ParseExpression(scope, viaNode.Value), ParseExpression(scope, viaNode.Index));

                // Create array
                case CreateArrayNode createArrayNode:
                {
                    Element prev = null;
                    Element current = null;

                    for (int i = 0; i < createArrayNode.Values.Length; i++)
                    {
                        current = new V_Append()
                        {
                            ParameterValues = new object[2]
                        };

                        if (prev != null)
                            current.ParameterValues[0] = prev;
                        else
                            current.ParameterValues[0] = new V_EmptyArray();

                        current.ParameterValues[1] = ParseExpression(scope, createArrayNode.Values[i]);
                        prev = current;
                    }

                    return current ?? new V_EmptyArray();
                }

                // Seperator

            }

            throw new Exception();
        }

        Element ParseMethod(ScopeGroup scope, MethodNode methodNode, bool needsToBeValue)
        {
            methodNode.RelatedScopeGroup = scope;

            // Get the kind of method the method is (Method (Overwatch), Custom Method, or User Method.)
            var methodType = GetMethodType(UserMethods, methodNode.Name);
            if (methodType == null)
            {
                throw new SyntaxErrorException($"The method {methodNode.Name} does not exist.", methodNode.Range);
                //Diagnostics.Add(new Diagnostic($"The method {methodNode.Name} does not exist.", methodNode.Range));
                //return null;
            }

            Element method;
            switch (methodType)
            {
                case MethodType.Method:
                {
                    Type owMethod = Element.GetMethod(methodNode.Name);

                    method = (Element)Activator.CreateInstance(owMethod);
                    Parameter[] parameterData = owMethod.GetCustomAttributes<Parameter>().ToArray();
                    
                    List<object> parsedParameters = new List<object>();
                    for (int i = 0; i < parameterData.Length; i++)
                    {
                        if (methodNode.Parameters.Length > i)
                        {
                            // Parse the parameter.
                            parsedParameters.Add(ParseParameter(scope, methodNode.Parameters[i], methodNode.Name, parameterData[i]));
                        }
                        else 
                        {
                            if (parameterData[i].ParameterType == ParameterType.Value && parameterData[i].DefaultType == null)
                            {
                                throw new SyntaxErrorException($"Missing parameter \"{parameterData[i].Name}\" in the method \"{methodNode.Name}\" and no default type to fallback on.", methodNode.Range);
                                //Diagnostics.Add(new Diagnostic($"Missing parameter \"{parameterData[i].Name}\" in the method \"{methodNode.Name}\" and no default type to fallback on.", methodNode.Range));
                                //return null;
                            }
                            else
                                parsedParameters.Add(parameterData[i].GetDefault());
                        }
                    }

                    method.ParameterValues = parsedParameters.ToArray();
                    break;
                }

                case MethodType.CustomMethod:
                {
                    MethodInfo customMethod = CustomMethods.GetCustomMethod(methodNode.Name);
                    Parameter[] parameterData = customMethod.GetCustomAttributes<Parameter>().ToArray();
                    object[] parsedParameters = new Element[parameterData.Length];

                    for (int i = 0; i < parameterData.Length; i++)
                        if (methodNode.Parameters.Length > i)
                            parsedParameters[i] = ParseParameter(scope, methodNode.Parameters[i], methodNode.Name, parameterData[i]);
                        else
                        {
                            throw new SyntaxErrorException($"Missing parameter \"{parameterData[i].Name}\" in the method \"{methodNode.Name}\" and no default type to fallback on.", methodNode.Range);
                            //Diagnostics.Add(new Diagnostic($"Missing parameter \"{parameterData[i].Name}\" in the method \"{methodNode.Name}\" and no default type to fallback on.", methodNode.Range));
                            //return null;
                        }

                    MethodResult result = (MethodResult)customMethod.Invoke(null, new object[] { IsGlobal, VarCollection, parsedParameters });
                    switch (result.MethodType)
                    {
                        #warning todo replace throws with Diagnostics.Add
                        case CustomMethodType.Action:
                            if (needsToBeValue)
                                throw new IncorrectElementTypeException(methodNode.Name, true);
                            break;

                        case CustomMethodType.MultiAction_Value:
                        case CustomMethodType.Value:
                            if (!needsToBeValue)
                                throw new IncorrectElementTypeException(methodNode.Name, false);
                            break;
                    }

                    // Some custom methods have extra actions.
                    if (result.Elements != null)
                        Actions.AddRange(result.Elements);
                    method = result.Result;

                    break;
                }

                case MethodType.UserMethod:
                {
                    UserMethod userMethod = UserMethod.GetUserMethod(UserMethods, methodNode.Name);
                    
                    MethodStack lastMethod = MethodStack.FirstOrDefault(ms => ms.UserMethod == userMethod);
                    if (lastMethod != null)
                    {
                        ContinueSkip.Setup();

                        for (int i = 0; i < lastMethod.ParameterVars.Length; i++)
                            Actions.Add(lastMethod.ParameterVars[i].Push(ParseExpression(scope, methodNode.Parameters[i])));

                        ContinueSkip.SetSkipCount(lastMethod.ActionIndex);
                        Actions.Add(Element.Part<A_Loop>());

                        if (needsToBeValue)
                            method = lastMethod.Return.GetVariable();
                        else
                            method = null;
                    }
                    else
                    {
                        var methodScope = Root.Child();

                        // Add the parameter variables to the scope.
                        ParameterVar[] parameterVars = new ParameterVar[userMethod.Parameters.Length];
                        for (int i = 0; i < parameterVars.Length; i++)
                        {
                            if (methodNode.Parameters.Length > i)
                            {
                                // Create a new variable using the parameter input.
                                parameterVars[i] = VarCollection.AssignParameterVar(Actions, methodScope, IsGlobal, userMethod.Parameters[i].Name, methodNode.Range);
                                Actions.Add(parameterVars[i].Push(ParseExpression(scope, methodNode.Parameters[i])));
                            }
                            else 
                            {
                                throw new SyntaxErrorException($"Missing parameter \"{userMethod.Parameters[i].Name}\" in the method \"{methodNode.Name}\".", methodNode.Range);
                                //Diagnostics.Add(new Diagnostic($"Missing parameter \"{userMethod.Parameters[i].Name}\" in the method \"{methodNode.Name}\".", methodNode.Range));
                                //return null;
                            }
                        }

                        var returns = VarCollection.AssignVar(IsGlobal);

                        var stack = new MethodStack(userMethod, parameterVars, ContinueSkip.GetSkipCount(), returns);
                        MethodStack.Add(stack);

                        var userMethodScope = methodScope.Child();
                        userMethod.Block.RelatedScopeGroup = userMethodScope;
                        
                        ParseBlock(userMethodScope, userMethod.Block, true, returns);
                        // No return value if the method is being used as an action.
                        if (needsToBeValue)
                            method = returns.GetVariable();
                        else
                            method = null;

                        for (int i = 0; i < parameterVars.Length; i++)
                            Actions.Add(parameterVars[i].Pop());

                        MethodStack.Remove(stack);
                    }
                    break;
                }

                default: throw new NotImplementedException();
            }

            methodNode.RelatedElement = method;
            return method;
        }

        object ParseParameter(ScopeGroup scope, IExpressionNode node, string methodName, Parameter parameterData)
        {
            object value = null;

            switch (node)
            {
                case EnumNode enumNode:

                    if (parameterData.ParameterType != ParameterType.Enum)
                    {
                        throw new SyntaxErrorException($"Expected value type \"{parameterData.ValueType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\".", enumNode.Range);
                        //Diagnostics.Add(new Diagnostic($"Expected value type \"{parameterData.ValueType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\".", enumNode.Range));
                        //return null;
                    }

                    if (enumNode.Type != parameterData.EnumType.Name)
                    {
                        throw new SyntaxErrorException($"Expected enum type \"{parameterData.EnumType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\".", enumNode.Range);
                        //Diagnostics.Add(new Diagnostic($"Expected enum type \"{parameterData.EnumType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\".", enumNode.Range));
                        //return null;
                    }

                    try
                    {
                        value = Enum.Parse(parameterData.EnumType, enumNode.Value);
                    }
                    catch (Exception ex) when (ex is ArgumentNullException || ex is ArgumentException || ex is OverflowException)
                    {
                        throw new SyntaxErrorException($"The value {enumNode.Value} does not exist in the enum {enumNode.Type}.", enumNode.Range);
                        //Diagnostics.Add(new Diagnostic($"The value {enumNode.Value} does not exist in the enum {enumNode.Type}.", enumNode.Range));
                        //return null;
                    }
                    
                    break;

                default:

                    if (parameterData.ParameterType != ParameterType.Value)
                    {
                        throw new SyntaxErrorException($"Expected enum type \"{parameterData.EnumType.Name}\" on {methodName}'s parameter \"{parameterData.Name}\".", ((Node)node).Range);
                        //Diagnostics.Add(new Diagnostic($"Expected enum type \"{parameterData.EnumType.Name}\" on {methodName}'s parameter \"{parameterData.Name}\".", ((Node)node).Range));
                        //return null;
                    }

                    value = ParseExpression(scope, node);

                    Element element = value as Element;
                    ElementData elementData = element.GetType().GetCustomAttribute<ElementData>();

                    if (elementData.ValueType != Elements.ValueType.Any &&
                    !parameterData.ValueType.HasFlag(elementData.ValueType))
                    {
                        throw new SyntaxErrorException($"Expected value type \"{parameterData.ValueType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\", got \"{elementData.ValueType.ToString()}\" instead.", ((Node)node).Range);
                        //Diagnostics.Add(new Diagnostic($"Expected value type \"{parameterData.ValueType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\", got \"{elementData.ValueType.ToString()}\" instead.", ((Node)node).Range));
                        //return null;
                    }

                    break;
            }

            if (value == null)
            {
                throw new SyntaxErrorException("Could not parse parameter.", ((Node)node).Range);
                //Diagnostics.Add(new Diagnostic("Could not parse parameter.", ((Node)node).Range));
                //return null;
            }

            return value;
        }
    
        public static MethodType? GetMethodType(UserMethod[] userMethods, string name)
        {
            if (Element.GetMethod(name) != null)
                return MethodType.Method;
            if (CustomMethods.GetCustomMethod(name) != null)
                return MethodType.CustomMethod;
            if (UserMethod.GetUserMethod(userMethods, name) != null)
                return MethodType.UserMethod;
            return null;
        }

        public enum MethodType
        {
            Method,
            CustomMethod,
            UserMethod
        }

        void ParseBlock(ScopeGroup scopeGroup, BlockNode blockNode, bool fulfillReturns, Var returnVar)
        {
            if (scopeGroup == null)
                throw new ArgumentNullException(nameof(scopeGroup));

            blockNode.RelatedScopeGroup = scopeGroup;

            int returnSkipStart = ReturnSkips.Count;

            //returned = Var.AssignVar(IsGlobal);
            
            for (int i = 0; i < blockNode.Statements.Length; i++)
                ParseStatement(scopeGroup, blockNode.Statements[i], returnVar, i == blockNode.Statements.Length - 1);

            if (fulfillReturns)
            {
                for (int i = ReturnSkips.Count - 1; i >= returnSkipStart; i--)
                {
                    ReturnSkips[i].ParameterValues = new object[]
                    {
                        new V_Number(Actions.Count - 1 - Actions.IndexOf(ReturnSkips[i]))
                    };
                    ReturnSkips.RemoveAt(i);
                }
                //return returnVar.GetVariable();
            }

            //return null;
        }

        void ParseStatement(ScopeGroup scope, IStatementNode statement, Var returnVar, bool isLast)
        {
            switch (statement)
            {
                // Method
                case MethodNode methodNode:
                    Element method = ParseMethod(scope, methodNode, false);
                    if (method != null)
                        Actions.Add(method);
                    return;
                
                // Variable set
                case VarSetNode varSetNode:
                    ParseVarset(scope, varSetNode);
                    return;

                // For
                case ForEachNode forEachNode:
                {
                    ContinueSkip.Setup();

                    // The action the for loop starts on.
                    int forActionStartIndex = Actions.Count() - 1;

                    ScopeGroup forGroup = scope.Child();

                    // Create the for's temporary variable.
                    DefinedVar forTempVar = VarCollection.AssignDefinedVar(
                        scopeGroup: forGroup,
                        name      : forEachNode.Variable,
                        isGlobal  : IsGlobal,
                        range     : forEachNode.Range
                        );

                    // Reset the counter.
                    Actions.Add(forTempVar.SetVariable(new V_Number(0)));

                    // Parse the for's block.
                    ParseBlock(forGroup, forEachNode.Block, false, returnVar);

                    // Add the for's finishing elements
                    Actions.Add(forTempVar.SetVariable( // Indent the index by 1.
                        Element.Part<V_Add>
                        (
                            forTempVar.GetVariable(),
                            new V_Number(1)
                        )
                    ));

                    ContinueSkip.SetSkipCount(forActionStartIndex);

                    // The target array in the for statement.
                    Element forArrayElement = ParseExpression(scope, forEachNode.Array);

                    Actions.Add(Element.Part<A_LoopIf>( // Loop if the for condition is still true.
                        Element.Part<V_Compare>
                        (
                            forTempVar.GetVariable(),
                            Operators.LessThan,
                            Element.Part<V_CountOf>(forArrayElement)
                        )
                    ));

                    ContinueSkip.ResetSkip();
                    return;
                }

                // For
                case ForNode forNode:
                {
                    ContinueSkip.Setup();

                    // The action the for loop starts on.
                    int forActionStartIndex = Actions.Count() - 1;

                    ScopeGroup forGroup = scope.Child();

                    // Set the variable
                    if (forNode.VarSetNode != null)
                        ParseVarset(scope, forNode.VarSetNode);
                    if (forNode.DefineNode != null)
                        ParseDefine(scope, forNode.DefineNode);

                    // Parse the for's block.
                    ParseBlock(forGroup, forNode.Block, false, returnVar);

                    Element expression = null;
                    if (forNode.Expression != null)
                        expression = ParseExpression(forGroup, forNode.Expression);

                    // Check the expression
                    if (forNode.Expression != null) // If it has an expression
                    {                        
                        // Parse the statement
                        if (forNode.Statement != null)
                            ParseStatement(forGroup, forNode.Statement, returnVar, false);

                        ContinueSkip.SetSkipCount(forActionStartIndex);
                        Actions.Add(Element.Part<A_LoopIf>(expression));
                    }
                    // If there is no expression but there is a statement, parse the statement.
                    else if (forNode.Statement != null)
                    {
                        ParseStatement(forGroup, forNode.Statement, returnVar, false);
                        ContinueSkip.SetSkipCount(forActionStartIndex);
                        // Add the loop
                        Actions.Add(Element.Part<A_Loop>());
                    }

                    ContinueSkip.ResetSkip();
                    return;
                }

                // While
                case WhileNode whileNode:
                {
                    ContinueSkip.Setup();

                    // The action the while loop starts on.
                    int whileStartIndex = Actions.Count() - 2;

                    ScopeGroup whileGroup = scope.Child();

                    ParseBlock(whileGroup, whileNode.Block, false, returnVar);

                    ContinueSkip.SetSkipCount(whileStartIndex);

                    // Add the loop-if
                    Element expression = ParseExpression(scope, whileNode.Expression);
                    Actions.Add(Element.Part<A_LoopIf>(expression));

                    ContinueSkip.ResetSkip();
                    return;
                }

                // If
                case IfNode ifNode:
                {
                    A_SkipIf if_SkipIf = new A_SkipIf();
                    Actions.Add(if_SkipIf);

                    var ifScope = scope.Child();

                    // Parse the if body.
                    ParseBlock(ifScope, ifNode.IfData.Block, false, returnVar);

                    // Determines if the "Skip" action after the if block will be created.
                    // Only if there is if-else or else statements.
                    bool addIfSkip = ifNode.ElseIfData.Length > 0 || ifNode.ElseBlock != null;

                    // Update the initial SkipIf's skip count now that we know the number of actions the if block has.
                    // Add one to the body length if a Skip action is going to be added.
                    if_SkipIf.ParameterValues = new object[]
                    {
                        Element.Part<V_Not>(ParseExpression(scope, ifNode.IfData.Expression)),
                        new V_Number(Actions.Count - 1 - Actions.IndexOf(if_SkipIf) + (addIfSkip ? 1 : 0))
                    };

                    // Create the "Skip" action.
                    A_Skip if_Skip = new A_Skip();
                    if (addIfSkip)
                    {
                        Actions.Add(if_Skip);
                    }

                    // Parse else-ifs
                    A_Skip[] elseif_Skips = new A_Skip[ifNode.ElseIfData.Length]; // The ElseIf's skips
                    for (int i = 0; i < ifNode.ElseIfData.Length; i++)
                    {
                        // Create the SkipIf action for the else if.
                        A_SkipIf elseif_SkipIf = new A_SkipIf();
                        Actions.Add(elseif_SkipIf);

                        // Parse the else-if body.
                        var elseifScope = scope.Child();
                        ParseBlock(elseifScope, ifNode.ElseIfData[i].Block, false, returnVar);

                        // Determines if the "Skip" action after the else-if block will be created.
                        // Only if there is additional if-else or else statements.
                        bool addIfElseSkip = i < ifNode.ElseIfData.Length - 1 || ifNode.ElseBlock != null;

                        // Set the SkipIf's parameters.
                        elseif_SkipIf.ParameterValues = new object[]
                        {
                            Element.Part<V_Not>(ParseExpression(scope, ifNode.ElseIfData[i].Expression)),
                            new V_Number(Actions.Count - 1 - Actions.IndexOf(elseif_SkipIf) + (addIfElseSkip ? 1 : 0))
                        };

                        // Create the "Skip" action for the else-if.
                        if (addIfElseSkip)
                        {
                            elseif_Skips[i] = new A_Skip();
                            Actions.Add(elseif_Skips[i]);
                        }
                    }

                    // Parse else body.
                    if (ifNode.ElseBlock != null)
                    {
                        var elseScope = scope.Child();
                        ParseBlock(elseScope, ifNode.ElseBlock, false, returnVar);
                    }

                    // Replace dummy skip with real skip now that we know the length of the if, if-else, and else's bodies.
                    // Replace if's dummy.
                    if_Skip.ParameterValues = new object[]
                    {
                        new V_Number(Actions.Count - 1 - Actions.IndexOf(if_Skip))
                    };

                    // Replace else-if's dummy.
                    for (int i = 0; i < elseif_Skips.Length; i++)
                    {
                        elseif_Skips[i].ParameterValues = new object[]
                        {
                            new V_Number(Actions.Count - 1 - Actions.IndexOf(elseif_Skips[i]))
                        };
                    }

                    return;
                }
                
                // Return
                case ReturnNode returnNode:

                    if (returnNode.Value != null)
                    {
                        Element result = ParseExpression(scope, returnNode.Value);
                        Actions.Add(returnVar.SetVariable(result));
                    }

                    if (!isLast)
                    {
                        A_Skip returnSkip = new A_Skip();
                        Actions.Add(returnSkip);
                        ReturnSkips.Add(returnSkip);
                    }

                    return;
                
                // Define
                case ScopedDefineNode defineNode:
                    ParseDefine(scope, defineNode);
                    return;
            }
        }

        void ParseVarset(ScopeGroup scope, VarSetNode varSetNode)
        {
            DefinedVar variable = scope.GetVar(varSetNode.Variable, varSetNode.Range, Diagnostics);

            Element target = null;
            if (varSetNode.Target != null) 
                target = ParseExpression(scope, varSetNode.Target);
            
            Element value = ParseExpression(scope, varSetNode.Value);

            Element initialVar = variable.GetVariable(target);

            Element index = null;
            if (varSetNode.Index != null)
            {
                index = ParseExpression(scope, varSetNode.Index);
                initialVar = Element.Part<V_ValueInArray>(initialVar, index);
            }


            switch (varSetNode.Operation)
            {
                case "+=":
                    value = Element.Part<V_Add>(initialVar, value);
                    break;

                case "-=":
                    value = Element.Part<V_Subtract>(initialVar, value);
                    break;

                case "*=":
                    value = Element.Part<V_Multiply>(initialVar, value);
                    break;

                case "/=":
                    value = Element.Part<V_Divide>(initialVar, value);
                    break;

                case "^=":
                    value = Element.Part<V_RaiseToPower>(initialVar, value);
                    break;

                case "%=":
                    value = Element.Part<V_Modulo>(initialVar, value);
                    break;
            }

            Actions.Add(variable.SetVariable(value, target, index));
        }

        void ParseDefine(ScopeGroup scope, ScopedDefineNode defineNode)
        {
            var var = VarCollection.AssignDefinedVar(scope, IsGlobal, defineNode.VariableName, defineNode.Range);

            // Set the defined variable if the variable is defined like "define var = 1"
            if (defineNode.Value != null)
                Actions.Add(var.SetVariable(ParseExpression(scope, defineNode.Value)));
        }
    }

    class TranslateResult
    {
        public readonly Rule Rule;
        public readonly Diagnostic[] Diagnostics;

        public TranslateResult(Rule rule, Diagnostic[] diagnostics)
        {
            Rule = rule;
            Diagnostics = diagnostics;
        }
    }
}