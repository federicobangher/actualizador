#!/usr/bin/env bash
# Verificación sin Windows (integración continua en Linux): compila todo (la interfaz, para .NET Framework 4.8 contra los
# ensamblados de referencia de Mono) y corre las pruebas del motor en .NET 8. Necesita dotnet-sdk-8.0 y mono-devel.
set -euo pipefail
raiz="$(cd "$(dirname "$0")/.." && pwd)"
cfg="$raiz/build/nuget.offline.config"
cd "$raiz"
dotnet build Actualizador.sln -nologo -v q -p:RestoreConfigFile="$cfg"
bin="$raiz/tests/Actualizador.Tests/bin/Debug/net8.0"
dotnet "$bin/Actualizador.Tests.dll" "$bin/samples"
