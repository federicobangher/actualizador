// Tipos que el compilador necesita para C# moderno cuando se compila para .NET Framework 4.8 (sólo verificación en Linux).
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
