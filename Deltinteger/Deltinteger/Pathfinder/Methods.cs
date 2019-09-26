using System;
using System.Collections.Generic;
using System.Linq;
using Deltin.Deltinteger.Pathfinder;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.Parse;

namespace Deltin.Deltinteger.Pathfinder
{
    public abstract class PathfindPlayer : CustomMethodBase
    {
        protected override MethodResult Get()
        {
            if (TranslateContext.ParserData.PathfinderInfo == null)
                TranslateContext.ParserData.PathfinderInfo = new PathfinderInfo(TranslateContext.ParserData);
            PathfinderInfo info = TranslateContext.ParserData.PathfinderInfo;
            return Get(info);
        }

        protected abstract MethodResult Get(PathfinderInfo info);
    }

    [CustomMethod("GetPath", CustomMethodType.MultiAction_Value)]
    [VarRefParameter("Path Map")]
    [Parameter("Position", Elements.ValueType.Vector, null)]
    [Parameter("Destination", Elements.ValueType.Vector, null)]
    public class GetPath : CustomMethodBase
    {
        protected override MethodResult Get()
        {
            if (((VarRef)Parameters[0]).Var is PathMapVar == false)
                throw SyntaxErrorException.InvalidVarRefType(((VarRef)Parameters[0]).Var.Name, VarType.PathMap, ParameterLocations[0]);
            
            PathMapVar pathmap = (PathMapVar)((VarRef)Parameters[0]).Var;
            Element position               = (Element)Parameters[1];
            Element destination            = (Element)Parameters[2];

            DijkstraNormal algorithm = new DijkstraNormal(TranslateContext, pathmap, position, destination);
            algorithm.Get();
            return new MethodResult(null, algorithm.finalPath.GetVariable());
        }

        public override CustomMethodWiki Wiki()
        {
            return new CustomMethodWiki(
                "Gets the path to the destination.",
                // Parameters
                "The initial position.",
                "The final destination."
            );
        }
    }

    [CustomMethod("Pathfind", CustomMethodType.Action)]
    [Parameter("Player", Elements.ValueType.Player, null)]
    [VarRefParameter("Path Map")]
    [Parameter("Destination", Elements.ValueType.Vector, null)]
    class Pathfind : PathfindPlayer
    {
        override protected MethodResult Get(PathfinderInfo info)
        {
            if (((VarRef)Parameters[1]).Var is PathMapVar == false)
                throw SyntaxErrorException.InvalidVarRefType(((VarRef)Parameters[1]).Var.Name, VarType.PathMap, ParameterLocations[1]);
            
            Element player                 = (Element)Parameters[0];
            PathMapVar pathmap = (PathMapVar)((VarRef)Parameters[1]).Var;

            IndexedVar destination = TranslateContext.VarCollection.AssignVar(Scope, "Destination", TranslateContext.IsGlobal, null);
            TranslateContext.Actions.AddRange(destination.SetVariable((Element)Parameters[2]));

            DijkstraNormal algorithm = new DijkstraNormal(TranslateContext, pathmap, Element.Part<V_PositionOf>(player), destination.GetVariable());
            algorithm.Get();
            DijkstraBase.Pathfind(TranslateContext, info, pathmap, algorithm.finalPath.GetVariable(), player, destination.GetVariable());
            return new MethodResult(null, null);
        }

        override public CustomMethodWiki Wiki()
        {
            return new CustomMethodWiki(
                "Moves a player to the specified position by pathfinding.",
                // Parameters
                "The player to move.",
                "The path map.",
                "The destination to move the player to."
            );
        }
    }

    [CustomMethod("PathfindAll", CustomMethodType.Action)]
    [Parameter("Players", Elements.ValueType.Player, null)]
    [VarRefParameter("Path Map")]
    [Parameter("Destination", Elements.ValueType.Vector, null)]
    class PathfindAll : PathfindPlayer
    {
        protected override MethodResult Get(PathfinderInfo info)
        {
            if (((VarRef)Parameters[1]).Var is PathMapVar == false)
                throw SyntaxErrorException.InvalidVarRefType(((VarRef)Parameters[1]).Var.Name, VarType.PathMap, ParameterLocations[1]);

            IndexedVar players = TranslateContext.VarCollection.AssignVar(Scope, "Players", TranslateContext.IsGlobal, Variable.O, new int[0], null);
            TranslateContext.Actions.AddRange(players.SetVariable((Element)Parameters[0]));

            PathMapVar pathmap = (PathMapVar)((VarRef)Parameters[1]).Var;

            IndexedVar destination = TranslateContext.VarCollection.AssignVar(Scope, "Destination", TranslateContext.IsGlobal, null);
            TranslateContext.Actions.AddRange(destination.SetVariable((Element)Parameters[2]));

            DijkstraMultiSource algorithm = new DijkstraMultiSource(TranslateContext, info, pathmap, players.GetVariable(), destination.GetVariable());
            algorithm.Get();
            return new MethodResult(null, null);
        }

        public override CustomMethodWiki Wiki()
        {
            return new CustomMethodWiki(
                "Moves an array of players to the specified position by pathfinding.",
                // Parameters
                "The array of players to move.",
                "The path map.",
                "The destination to move the player to."
            );
        }
    }

    [CustomMethod("StopPathfind", CustomMethodType.Action)]
    [Parameter("Players", Elements.ValueType.Player, null)]
    class StopPathfind : PathfindPlayer
    {
        protected override MethodResult Get(PathfinderInfo info)
        {
            Element player = (Element)Parameters[0];

            return new MethodResult(ArrayBuilder<Element>.Build(
                info.Nodes.SetVariable(new V_EmptyArray(), player),
                info.Path.SetVariable(new V_EmptyArray(), player)
            ), null);
        }

        public override CustomMethodWiki Wiki()
        {
            return new CustomMethodWiki(
                "Stops pathfinding for the specified players.",
                "The players that will stop pathfinding."
            );
        }
    }

    [CustomMethod("IsPathfinding", CustomMethodType.Value)]
    [Parameter("Player", Elements.ValueType.Player, null)]
    class IsPathfinding : PathfindPlayer
    {
        override protected MethodResult Get(PathfinderInfo info)
        {
            Element player = (Element)Parameters[0];

            Element isPathfinding = new V_Compare(
                Element.Part<V_CountOf>(info.Path.GetVariable()),
                Operators.GreaterThan,
                new V_Number(0)
            );

            return new MethodResult(null, isPathfinding);
        }

        override public CustomMethodWiki Wiki()
        {
            return new CustomMethodWiki(
                "Checks if the target player is currently pathfinding with Pathfind().",
                "The player to check."
            );
        }
    }
}