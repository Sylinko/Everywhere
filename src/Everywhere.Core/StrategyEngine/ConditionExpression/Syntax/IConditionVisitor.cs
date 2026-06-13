namespace Everywhere.StrategyEngine.ConditionExpression.Syntax;

internal interface IConditionVisitor
{
    void Visit(ConditionScalarNode node);

    void Visit(ConditionChildrenNode node);

    void Visit(ConditionNotNode node);

    void Visit(ConditionPathNode node);
}

internal static class ConditionVisitorExtensions
{
    public static void VisitChildren(this IConditionVisitor visitor, IEnumerable<ConditionNode> children)
    {
        foreach (var child in children.AsValueEnumerable())
        {
            child.Accept(visitor);
        }
    }
}
