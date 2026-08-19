namespace Autobahn;

/// <summary>
/// Marks a static member that produces scenarios, so the CLI can find and run them without
/// the assembly having a <c>Main</c> that does it.
/// </summary>
/// <remarks>
/// The member must be a static property or a static parameterless method returning
/// <see cref="ScenarioProps"/> or a sequence of them. Marking is optional - the CLI will take
/// any public static member of the right shape - but an assembly that marks its scenarios
/// says which ones it meant, and stops an unrelated helper being mistaken for one.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class ScenarioSourceAttribute : Attribute;
