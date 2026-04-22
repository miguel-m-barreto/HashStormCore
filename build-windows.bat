@echo off
cd src\HashStormCore
dotnet publish -c Release --framework net10.0 -o ../../build
