#requires -Version 7.0
# Read-only source checks. Does not build, launch the app, or write command logs.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$problems = [Collections.Generic.List[string]]::new()
$sourceFiles = @(
    Get-ChildItem (Join-Path $projectRoot 'FluentFlyoutWPF/Windows/Island*.cs')
    Get-ChildItem (Join-Path $projectRoot 'FluentFlyoutWPF/Classes/Island*.cs')
    Get-ChildItem (Join-Path $projectRoot 'FluentFlyoutWPF/ViewModels/UserSettings*.cs')
    Get-Item (Join-Path $projectRoot 'FluentFlyoutWPF/Controls/IslandActionSurface.cs')
    Get-Item (Join-Path $projectRoot 'FluentFlyoutWPF/Controls/TaskbarWidgetExpandedContent.xaml.cs')
    Get-Item (Join-Path $projectRoot 'tools/DictationChecks/TaskbarContentChecks.cs')
    Get-Item (Join-Path $projectRoot 'tools/DictationChecks/TaskbarHostChecks.cs')
    Get-Item (Join-Path $projectRoot 'FluentFlyoutWPF/Pages/IslandPage.xaml.cs')
    Get-Item (Join-Path $projectRoot 'FluentFlyoutWPF/MainWindow.xaml.cs')
    Get-Item (Join-Path $projectRoot 'FluentFlyoutWPF/Classes/GoogleCalendarService.cs')
    Get-Item (Join-Path $projectRoot 'FluentFlyoutWPF/Classes/Settings/SettingsManager.cs')
    Get-Item (Join-Path $projectRoot 'FluentFlyoutWPF/Classes/SessionDiagnostics.cs')
    Get-ChildItem (Join-Path $projectRoot 'tools/DictationChecks/Island*Checks.cs')
    Get-Item (Join-Path $projectRoot '.selfcheck/Program.cs')
) | Sort-Object FullName -Unique

$syntaxTrees = @{}
try {
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
    foreach ($file in $sourceFiles) {
        $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName), $options, $file.FullName)
        $syntaxTrees[$file.FullName] = $tree
        foreach ($diagnostic in $tree.GetDiagnostics()) {
            if ($diagnostic.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) {
                $problems.Add("$($file.Name): $diagnostic")
            }
        }
        foreach ($node in $tree.GetRoot().DescendantNodes()) {
            if ($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax] -and
                $node.Expression -is [Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax]) {
                $member = $node.Expression.Name.Identifier.ValueText
                if ($member -in @('AddIslandShelfPaths', 'RemoveIslandShelfItem') -or
                    ($member -eq 'Copy' -and $node.Expression.Expression.ToString() -eq '_clipboard')) {
                    $problems.Add("$($file.Name): llamada a una API sustituida: $member")
                }
            }
            if ($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax] -and
                $node.Left.ToString() -eq '_nextSeekRead' -and $node.Right.ToString() -match 'DateTimeOffset') {
                $problems.Add("$($file.Name): cadencia multimedia mezclada con reloj civil")
            }
        }
    }
} catch {
    throw "El analizador C# de PowerShell no está disponible. No se ha compilado ni ejecutado la aplicación. $($_.Exception.Message)"
}

$xamlPaths = @('FluentFlyoutWPF/Windows/IslandWindow.xaml', 'FluentFlyoutWPF/Pages/IslandPage.xaml',
    'FluentFlyoutWPF/MainWindow.xaml', 'FluentFlyoutWPF/Resources/Localization/Dictionary-en-US.xaml',
    'FluentFlyoutWPF/Resources/Localization/Dictionary-es.xaml')
$documents = @{}
foreach ($relativePath in $xamlPaths) {
    $path = Join-Path $projectRoot $relativePath
    $documents[$relativePath] = [System.Xml.Linq.XDocument]::Load($path)
}
$baseKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($attribute in $documents['FluentFlyoutWPF/Resources/Localization/Dictionary-en-US.xaml'].Descendants().Attributes()) {
    if ($attribute.Name.LocalName -eq 'Key') { [void]$baseKeys.Add($attribute.Value) }
}
$handlerCount = 0
$events = @('Activated', 'Click', 'Loaded', 'SizeChanged', 'TextChanged', 'SelectionChanged', 'PreviewKeyDown',
    'KeyDown', 'LostKeyboardFocus', 'LostMouseCapture', 'MouseLeftButtonDown', 'MouseLeftButtonUp',
    'MouseEnter', 'MouseLeave', 'MouseMove', 'MouseWheel', 'PreviewMouseWheel', 'PreviewMouseMove',
    'PreviewMouseLeftButtonDown', 'PreviewMouseLeftButtonUp', 'DragOver', 'DragLeave', 'Drop', 'ValueChanged')
foreach ($relativePath in $xamlPaths | Where-Object { $_ -notmatch '/Localization/' }) {
    $document = $documents[$relativePath]
    $localKeys = [Collections.Generic.HashSet[string]]::new($baseKeys, [StringComparer]::Ordinal)
    foreach ($attribute in $document.Descendants().Attributes()) {
        if ($attribute.Name.LocalName -eq 'Key') { [void]$localKeys.Add($attribute.Value) }
    }
    $methodNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $ownerFiles = switch -Wildcard ($relativePath) {
        '*IslandWindow.xaml' { $sourceFiles | Where-Object Name -like 'IslandWindow*.cs' }
        '*IslandPage.xaml' { $sourceFiles | Where-Object Name -eq 'IslandPage.xaml.cs' }
        '*MainWindow.xaml' { $sourceFiles | Where-Object Name -eq 'MainWindow.xaml.cs' }
    }
    foreach ($file in $ownerFiles) {
        foreach ($node in $syntaxTrees[$file.FullName].GetRoot().DescendantNodes()) {
            if ($node -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]) { [void]$methodNames.Add($node.Identifier.ValueText) }
        }
    }
    foreach ($attribute in $document.Descendants().Attributes()) {
        if ($events -contains $attribute.Name.LocalName -and $attribute.Value -match '^\w+$') {
            $handlerCount++
            if (!$methodNames.Contains($attribute.Value)) { $problems.Add("${relativePath}: manejador ausente $($attribute.Value)") }
        }
        if ($attribute.Value -match '^\{DynamicResource (Island\w+)\}$' -and !$localKeys.Contains($Matches[1])) {
            $problems.Add("${relativePath}: recurso ausente $($Matches[1])")
        }
    }
}
foreach ($relativePath in $xamlPaths | Where-Object { $_ -match '/Localization/' }) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($attribute in $documents[$relativePath].Descendants().Attributes()) {
        if ($attribute.Name.LocalName -eq 'Key' -and !$seen.Add($attribute.Value)) { $problems.Add("${relativePath}: recurso duplicado $($attribute.Value)") }
    }
}
if ($problems.Count -gt 0) { throw ($problems -join [Environment]::NewLine) }
Write-Output "Correcto: sintaxis C# de $($sourceFiles.Count) archivos, XML de $($xamlPaths.Count) archivos, $handlerCount manejadores y recursos Island."
Write-Output 'Comprobación estática: no confirma compilación, ejecución ni apariencia visual.'
