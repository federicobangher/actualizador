// Actualizador sólo funciona en Windows (registro, winget, Windows Update, firmas Authenticode).
#if NET5_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
