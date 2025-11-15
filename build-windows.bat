@echo off
cd src\HashStormCore
dotnet publish -c Release --framework net6.0 -o ../../build
