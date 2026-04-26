@echo off
setlocal enabledelayedexpansion

if [%1] == [] GOTO :USAGE
set version=%1
set fileversion=%1
set informationalversion=%1

if [%2] NEQ [] SET fileversion=%2
if [%3] NEQ [] SET informationalversion=%3

echo Setting version in *.csproj files
set "script=%TEMP%\setversion-csproj.ps1"
(
echo param^(
echo   [string]$Version,
echo   [string]$FileVersion,
echo   [string]$InformationalVersion
echo ^)
echo $ErrorActionPreference = "Stop"
echo $projects = Get-ChildItem -Path ^(Get-Location^) -Filter *.csproj -Recurse
echo foreach ^($project in $projects^) ^{
echo   Write-Host "^> $($project.FullName)"
echo   [xml]$xml = Get-Content -Path $project.FullName
echo   $propertyGroup = $xml.Project.PropertyGroup ^| Select-Object -First 1
echo   if ^(-not $propertyGroup^) ^{
echo     $propertyGroup = $xml.CreateElement^("PropertyGroup"^)
echo     [void]$xml.Project.AppendChild^($propertyGroup^)
echo   ^}
echo   function Set-Or-CreateTag ^([xml]$doc, [System.Xml.XmlElement]$group, [string]$tagName, [string]$value^) ^{
echo     $node = $group.SelectSingleNode^($tagName^)
echo     if ^($node^) ^{
echo       $node.InnerText = $value
echo     ^} else ^{
echo       $newNode = $doc.CreateElement^($tagName^)
echo       $newNode.InnerText = $value
echo       [void]$group.AppendChild^($newNode^)
echo     ^}
echo   ^}
echo   Set-Or-CreateTag $xml $propertyGroup "Version" $Version
echo   Set-Or-CreateTag $xml $propertyGroup "AssemblyVersion" $Version
echo   Set-Or-CreateTag $xml $propertyGroup "FileVersion" $FileVersion
echo   Set-Or-CreateTag $xml $propertyGroup "InformationalVersion" $InformationalVersion
echo   $xml.Save^($project.FullName^)
echo ^}
) > "%script%"

powershell -NoProfile -ExecutionPolicy Bypass -File "%script%" "%version%" "%fileversion%" "%informationalversion%"
set exit_code=%ERRORLEVEL%
del /q "%script%" >nul 2>&1
if not %exit_code%==0 exit /b %exit_code%
echo Done!

GOTO:EOF

:USAGE
echo Usage:
echo.
echo SetVersion.cmd Version [FileVersion] [InformationalVersion]
echo.