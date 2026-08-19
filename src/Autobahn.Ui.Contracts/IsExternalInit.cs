#if !NET
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

/// <summary>
/// The marker the compiler needs to emit an <c>init</c> accessor.
/// </summary>
/// <remarks>
/// It ships with .NET 5 and later but not with <c>netstandard2.0</c>, and the compiler is
/// happy to take one the assembly declares itself. Without it, records and init-only
/// properties do not compile here - which is the one language-level thing the framework
/// constraint actually costs, and it costs eight lines.
/// </remarks>
internal static class IsExternalInit;
#endif
