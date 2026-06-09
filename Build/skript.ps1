#Requires -Version 5.1
#Requires -RunAsAdministrator

#------------------------------------------------------------------------------------
# Windows DaSi Tool - WPF GUI Version
#------------------------------------------------------------------------------------

Add-Type -AssemblyName PresentationCore, PresentationFramework
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Konsole ausblenden
Add-Type -Name Window -Namespace Console -MemberDefinition '
[DllImport("Kernel32.dll")]
public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")]
public static extern bool ShowWindow(IntPtr hWnd, Int32 nCmdShow);
'
$consolePtr = [Console.Window]::GetConsoleWindow()
[Console.Window]::ShowWindow($consolePtr, 0) | Out-Null

$script:VersionString = "0.8.2"
$script:BuildString = "GUI-Edition"
$WindowTitle = "Windows DaSi Tool $script:VersionString"

$script:UpdateCheckUrl = "https://raw.githubusercontent.com/Tinnitus97/backup_my_windows_Updater/main/newversion.txt"
$script:ProjectUrl     = "https://github.com/Tinnitus97/backup_my_windows"

#----Hilfsfunktionen (Main Thread)----------------------------#

function Test-IsNtfsDrive {
    param([string]$Path)
    try {
        $driveRoot = [System.IO.Path]::GetPathRoot($Path)
        if ($driveRoot.StartsWith("\")) { return $true }
        $driveInfo = [System.IO.DriveInfo]::new($driveRoot)
        if ($driveInfo.DriveFormat -ine "NTFS") { return $false }
        return $true
    }
    catch { return $false }
}

function Select-FolderDialog {
    param ([string]$Description)
    try {
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = $Description
        $dialog.ValidateNames = $false
        $dialog.CheckFileExists = $false
        $dialog.CheckPathExists = $true
        $dialog.FileName = "Ordner_auswaehlen" 
        $dialog.Filter = "Ordner|\."

        $form = New-Object System.Windows.Forms.Form
        $form.StartPosition = 'CenterScreen'
        $form.Size = [System.Drawing.Size]::new(0, 0)
        $form.ShowInTaskbar = $false
        $form.TopMost = $true

        $null = $form.Show()
        $result = $dialog.ShowDialog($form)
        $form.Dispose()

        if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
            return [System.IO.Path]::GetDirectoryName($dialog.FileName)
        }
    } catch {}
    return $null
}

#----XAML Definition (100% PS2EXE Safe mit XML Entitaeten)---#
$Xaml = @"
<Window
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="$WindowTitle"
    MinWidth="1200" MinHeight="650"
    Width="1300" Height="750"
    WindowStartupLocation="CenterScreen"
    Background="#1E1E2E"
    Topmost="False">

    <Window.Resources>
        <Style x:Key="PathBox" TargetType="TextBox">
            <Setter Property="Background" Value="#11111B"/>
            <Setter Property="Foreground" Value="#CDD6F4"/>
            <Setter Property="BorderBrush" Value="#45475A"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="4"/>
            <Setter Property="IsReadOnly" Value="True"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
        </Style>

        <Style x:Key="CardToggle" TargetType="ToggleButton">
            <Setter Property="Background" Value="#2A2A3E"/>
            <Setter Property="Foreground" Value="#CDD6F4"/>
            <Setter Property="BorderBrush" Value="#45475A"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="8,10"/>
            <Setter Property="Margin" Value="4,3"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Height" Value="40"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ToggleButton">
                        <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5" Padding="{TemplateBinding Padding}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>
                                <Border x:Name="CheckMark" Width="16" Height="16" CornerRadius="3" BorderBrush="#6C7086" BorderThickness="1" Background="#11111B" Margin="0,0,10,0">
                                    <Path x:Name="CheckIcon" Data="M3,8 L6,11 L13,4" Stroke="#A6E3A1" StrokeThickness="2" Visibility="Hidden" Stretch="None" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                </Border>
                                <TextBlock Grid.Column="1" Text="{TemplateBinding Content}" TextWrapping="Wrap" VerticalAlignment="Center"/>
                            </Grid>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter Property="Background" Value="#313244"/>
                                <Setter Property="BorderBrush" Value="#89B4FA"/>
                            </Trigger>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter Property="Background" Value="#313244"/>
                                <Setter Property="BorderBrush" Value="#A6E3A1"/>
                                <Setter TargetName="CheckIcon" Property="Visibility" Value="Visible"/>
                                <Setter TargetName="CheckMark" Property="BorderBrush" Value="#A6E3A1"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.35"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="ActionBtn" TargetType="Button">
            <Setter Property="Background" Value="#89B4FA"/>
            <Setter Property="Foreground" Value="#11111B"/>
            <Setter Property="FontWeight" Value="Bold"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="8,10"/>
            <Setter Property="Margin" Value="4,10,4,4"/>
            <Setter Property="Height" Value="45"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border Background="{TemplateBinding Background}" CornerRadius="5">
                            <TextBlock Text="{TemplateBinding Content}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter Property="Background" Value="#B4BEFE"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.4"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="SecondaryBtn" TargetType="Button">
            <Setter Property="Background" Value="#313244"/>
            <Setter Property="Foreground" Value="#CDD6F4"/>
            <Setter Property="BorderBrush" Value="#45475A"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Margin" Value="4,3"/>
            <Setter Property="Height" Value="40"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5">
                            <TextBlock Text="{TemplateBinding Content}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter Property="Background" Value="#45475A"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.35"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="ExitBtn" TargetType="Button" BasedOn="{StaticResource SecondaryBtn}">
            <Setter Property="Foreground" Value="#F38BA8"/>
            <Setter Property="BorderBrush" Value="#F38BA8"/>
        </Style>

        <Style x:Key="SectionLabel" TargetType="TextBlock">
            <Setter Property="Foreground" Value="#89B4FA"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Margin" Value="4,16,4,4"/>
        </Style>
    </Window.Resources>

    <Grid Margin="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="6*"/>
            <ColumnDefinition Width="4*"/>
        </Grid.ColumnDefinitions>

        <Grid Grid.Column="0" Margin="0,0,10,0">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <StackPanel Grid.Row="0" Margin="0,0,0,10">
                <TextBlock Style="{StaticResource SectionLabel}" Text="Optionen &amp; Verzeichnisse" Margin="4,0,4,4"/>
                <UniformGrid Columns="4" Margin="0,0,0,8">
                    <Button Name="BtnClearPaths" Style="{StaticResource SecondaryBtn}" Content="Pfade leeren"/>
                    <ToggleButton Name="TgB_Logging" Style="{StaticResource CardToggle}" Content="Logging: Minimal / AUS"/>
                    <ToggleButton Name="TgB_AutoUpdate" Style="{StaticResource CardToggle}" Content="Apps updaten: AUS"/>
                    <Button Name="BtnExit" Style="{StaticResource ExitBtn}" Content="Beenden"/>
                </UniformGrid>

                <Grid Margin="4,2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="120"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="40"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="Benutzerprofil:" Foreground="#CDD6F4" VerticalAlignment="Center"/>
                    <TextBox Name="TbSourcePath" Grid.Column="1" Style="{StaticResource PathBox}" Text="{Binding [2]}"/>
                    <Button Name="BtnSelSource" Grid.Column="2" Content="..." Margin="4,0,0,0" Background="#313244" Foreground="#CDD6F4" BorderBrush="#45475A"/>
                </Grid>
                <Grid Margin="4,2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="120"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="40"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="Backup-Ziel/Quelle:" Foreground="#CDD6F4" VerticalAlignment="Center"/>
                    <TextBox Name="TbBackupPath" Grid.Column="1" Style="{StaticResource PathBox}" Text="{Binding [3]}"/>
                    <Button Name="BtnSelBackup" Grid.Column="2" Content="..." Margin="4,0,0,0" Background="#313244" Foreground="#CDD6F4" BorderBrush="#45475A"/>
                </Grid>
            </StackPanel>

            <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
                <StackPanel>
                    <TextBlock Style="{StaticResource SectionLabel}" Text="SICHERN (Backup)"/>
                    <UniformGrid Columns="2">
                        <ToggleButton Name="TgB_User" Style="{StaticResource CardToggle}" Content="Windows Benutzerprofil"/>
                        <ToggleButton Name="TgB_Firefox" Style="{StaticResource CardToggle}" Content="Firefox-Profil"/>
                        <ToggleButton Name="TgB_Edge" Style="{StaticResource CardToggle}" Content="Edge-Profil"/>
                        <ToggleButton Name="TgB_Chrome" Style="{StaticResource CardToggle}" Content="Chrome-Profil"/>
                        <ToggleButton Name="TgB_Brave" Style="{StaticResource CardToggle}" Content="Brave-Profil"/>
                        <ToggleButton Name="TgB_Thunderbird" Style="{StaticResource CardToggle}" Content="Thunderbird-Profil"/>
                        <ToggleButton Name="TgB_Winget" Style="{StaticResource CardToggle}" Content="Programme exportieren (Winget)"/>
                        <ToggleButton Name="TgB_Wlan" Style="{StaticResource CardToggle}" Content="WLAN Profile exportieren"/>
                    </UniformGrid>

                    <TextBlock Style="{StaticResource SectionLabel}" Text="WIEDERHERSTELLEN (Restore)"/>
                    <UniformGrid Columns="2">
                        <ToggleButton Name="TgR_User" Style="{StaticResource CardToggle}" Content="Windows Benutzerprofil"/>
                        <ToggleButton Name="TgR_Firefox" Style="{StaticResource CardToggle}" Content="Firefox-Profil"/>
                        <ToggleButton Name="TgR_Edge" Style="{StaticResource CardToggle}" Content="Edge-Profil"/>
                        <ToggleButton Name="TgR_Chrome" Style="{StaticResource CardToggle}" Content="Chrome-Profil"/>
                        <ToggleButton Name="TgR_Brave" Style="{StaticResource CardToggle}" Content="Brave-Profil"/>
                        <ToggleButton Name="TgR_Thunderbird" Style="{StaticResource CardToggle}" Content="Thunderbird-Profil"/>
                        <ToggleButton Name="TgR_Winget" Style="{StaticResource CardToggle}" Content="Programme installieren (Winget)"/>
                        <ToggleButton Name="TgR_Wlan" Style="{StaticResource CardToggle}" Content="WLAN Profile importieren"/>
                    </UniformGrid>
                </StackPanel>
            </ScrollViewer>

            <Button Name="BtnExecute" Grid.Row="2" Style="{StaticResource ActionBtn}" Content="Ausgew&#228;hlte Aktionen starten"/>
        </Grid>

        <Grid Grid.Column="1">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" Text="Aktivit&#228;ts-Protokoll" Foreground="#CDD6F4" FontSize="13" FontWeight="SemiBold" Margin="0,0,0,8"/>

            <Border Name="UpdateBanner" Grid.Row="0" Background="#313244" BorderBrush="#F9E2AF" BorderThickness="1"
                    CornerRadius="5" Padding="8,6" Margin="0,26,0,0" Visibility="Collapsed" Cursor="Hand">
                <StackPanel>
                    <TextBlock Name="UpdateBannerText" Foreground="#F9E2AF" FontSize="11" FontWeight="SemiBold"
                               TextWrapping="Wrap" Text="&#x1F504; Neue Version verfuegbar!"/>
                    <TextBlock Foreground="#A6ADC8" FontSize="10" Margin="0,2,0,0"
                               Text="Klicken, um die Projektseite zu oeffnen."/>
                </StackPanel>
            </Border>
            <TextBox Name="TBLog" Grid.Row="1" Background="#11111B" Foreground="#A6E3A1" BorderBrush="#313244" BorderThickness="1" 
                     FontFamily="Consolas" FontSize="11" IsReadOnly="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" 
                     Padding="6" Text="{Binding [0]}" VerticalContentAlignment="Top"/>
        </Grid>
    </Grid>
</Window>
"@

#----GUI Initialisierung & Binding-----------------------------#
$Window = [Windows.Markup.XamlReader]::Parse($Xaml)
[xml]$xmlParsed = $Xaml
$xmlParsed.SelectNodes("//*[@Name]") | ForEach-Object {
    Set-Variable -Name $_.Name -Value $Window.FindName($_.Name)
}

$DataContext = New-Object System.Collections.ObjectModel.ObservableCollection[Object]
$State = [pscustomobject]@{}

function FillDataContext ($props) {
    for ($i = 0; $i -lt $props.Length; $i++) {
        $DataContext.Add($null)
        $getter = [scriptblock]::Create("return `$DataContext['$i']")
        $setter = [scriptblock]::Create("param(`$val) `$DataContext['$i']=`$val")
        $State | Add-Member -Name $props[$i] -MemberType ScriptProperty -Value $getter -SecondValue $setter
    }
}
FillDataContext @("LogText", "UIEnabled", "SourcePath", "BackupPath")
$Window.DataContext = $DataContext

# Log-Text ohne Umlaute
$State.LogText = "Warte auf Eingabe...`nBitte waehle zuerst die benoetigten Pfade.`n"
$State.UIEnabled = $true
$State.SourcePath = ""
$State.BackupPath = ""

function Set-Binding {
    param($Target, $Property, $Index)
    $Binding = New-Object System.Windows.Data.Binding
    $Binding.Path = "[$Index]"
    $Binding.Mode = [System.Windows.Data.BindingMode]::TwoWay
    [void]$Target.SetBinding($Property, $Binding)
}

Set-Binding $TBLog ([System.Windows.Controls.TextBox]::TextProperty) 0

# ======================================================================
# AUTO-SCROLL LOGIK (Kombination aus TextChanged und deiner Suchfunktion)
# ======================================================================
$SyncHash.LogAutoScroll = $true

# 1. TextChanged fängt jede Änderung ab und scrollt, wenn es erlaubt ist
$TBLog.Add_TextChanged({
    if ($SyncHash.LogAutoScroll) {
        $TBLog.ScrollToEnd()
    }
})

# 2. Hilfsfunktion, um den echten internen ScrollViewer zu finden
function Find-ScrollViewer ($element) {
    if ($null -eq $element) { return $null }
    $count = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($element)
    for ($i = 0; $i -lt $count; $i++) {
        $child = [System.Windows.Media.VisualTreeHelper]::GetChild($element, $i)
        if ($child -is [System.Windows.Controls.ScrollViewer]) { return $child }
        $found = Find-ScrollViewer $child
        if ($null -ne $found) { return $found }
    }
    return $null
}
# ======================================================================

Set-Binding $TbSourcePath ([System.Windows.Controls.TextBox]::TextProperty) 2
Set-Binding $TbBackupPath ([System.Windows.Controls.TextBox]::TextProperty) 3

# UpdateBanner Click -> Projektseite öffnen
$UpdateBanner.Add_MouseLeftButtonUp({
    Start-Process $script:ProjectUrl
})

$ToggleButtons = @(
    "TgB_User","TgB_Firefox","TgB_Edge","TgB_Chrome","TgB_Brave","TgB_Thunderbird","TgB_Winget","TgB_Wlan",
    "TgR_User","TgR_Firefox","TgR_Edge","TgR_Chrome","TgR_Brave","TgR_Thunderbird","TgR_Winget","TgR_Wlan",
    "BtnSelSource", "BtnSelBackup", "BtnClearPaths", "TgB_Logging", "TgB_AutoUpdate", "BtnExecute"
)
foreach ($btn in $ToggleButtons) {
    $ctrl = $Window.FindName($btn)
    if ($ctrl) { Set-Binding $ctrl ([System.Windows.Controls.Control]::IsEnabledProperty) 1 }
}

#----Event Handler für UI & Pfade-----------------------#

$TgB_Logging.Add_Click({
    if ($TgB_Logging.IsChecked) { $TgB_Logging.Content = "Logging: Detailliert / AN" }
    else { $TgB_Logging.Content = "Logging: Minimal / AUS" }
})

$TgB_AutoUpdate.Add_Click({
    if ($TgB_AutoUpdate.IsChecked) { $TgB_AutoUpdate.Content = "Apps updaten: AN" }
    else { $TgB_AutoUpdate.Content = "Apps updaten: AUS" }
})

$BtnClearPaths.Add_Click({
    $State.SourcePath = ""
    $State.BackupPath = ""
    $State.LogText += ">> Pfade wurden geleert.`r`n"
})

$BtnSelSource.Add_Click({
    $folder = Select-FolderDialog -Description "Quell-Benutzerprofil auswaehlen (z.B. C:\Users\Name)"
    if ($folder) { $State.SourcePath = $folder }
})

$BtnSelBackup.Add_Click({
    $folder = Select-FolderDialog -Description "Backup Basis-Verzeichnis auswaehlen"
    if ($folder) {
        if (Test-IsNtfsDrive -Path $folder) { $State.BackupPath = $folder } 
        else { [System.Windows.MessageBox]::Show("Das ausgewaehlte Laufwerk ist NICHT mit NTFS formatiert!`nRobocopy benoetigt zwingend NTFS.", "Fehler", [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Error) }
    }
})

$Window.Add_Closing({
    if (-not $State.UIEnabled) {
        $res = [System.Windows.MessageBox]::Show("Es laeuft gerade eine Aktion!`nSoll diese wirklich hart abgebrochen und das Programm beendet werden?", "Abbruch Bestaetigen", [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Warning)
        if ($res -eq 'Yes') {
            $SyncHash.CancelRequested = $true
            foreach ($procId in $SyncHash.ActiveProcesses.ToArray()) {
                Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
            }
            Stop-Process -Name winget -Force -ErrorAction SilentlyContinue
        } else {
            $_.Cancel = $true
        }
    }
})

$BtnExit.Add_Click({ $Window.Close() })

#----Ausführungs-Logik (Runspace / Async) & Prozess-Tracking--#
$Global:SyncHash = [hashtable]::Synchronized(@{})
$SyncHash.Window = $Window
$SyncHash.ActiveProcesses = [System.Collections.ArrayList]::Synchronized([System.Collections.ArrayList]::new())
$SyncHash.CancelRequested = $false

$Jobs = [System.Collections.ArrayList]::Synchronized([System.Collections.ArrayList]::new())
$InitialSessionState = [initialsessionstate]::CreateDefault()

function Start-RunspaceTask {
    param([scriptblock]$ScriptBlock, [PSObject[]]$ProxyVars)
    $Runspace = [runspacefactory]::CreateRunspace($InitialSessionState)
    $Runspace.ApartmentState = 'STA'
    $Runspace.ThreadOptions = 'ReuseThread'
    $Runspace.Open()
    foreach ($Var in $ProxyVars) { $Runspace.SessionStateProxy.SetVariable($Var.Name, $Var.Variable) }
    $Thread = [powershell]::Create('NewRunspace')
    $Thread.AddScript($ScriptBlock) | Out-Null
    $Thread.Runspace = $Runspace
    [void]$Jobs.Add([psobject]@{ PowerShell = $Thread; Runspace = $Thread.BeginInvoke() })
}

$JobCleanupScript = {
    do {
        foreach ($Job in $Jobs.ToArray()) {
            if ($Job.Runspace.IsCompleted) {
                try { [void]$Job.PowerShell.EndInvoke($Job.Runspace) } catch {}
                $Job.PowerShell.Runspace.Close()
                $Job.PowerShell.Dispose()
                $Jobs.Remove($Job)
            }
        }
        Start-Sleep -Milliseconds 500
    } while ($SyncHash.CleanupJobs)

    # Nach Shutdown: verbleibende (noch laufende) Jobs forciert beenden
    foreach ($Job in $Jobs.ToArray()) {
        try {
            $Job.PowerShell.Stop()
            $Job.PowerShell.Runspace.Close()
            $Job.PowerShell.Dispose()
        } catch {}
    }
}

function Run-Async ($scriptBlock) {
    Start-RunspaceTask $scriptBlock @(
        [psobject]@{ Name = 'DataContext'; Variable = $DataContext },
        [psobject]@{ Name = 'State';       Variable = $State       },
        [psobject]@{ Name = 'SyncHash';    Variable = $SyncHash    }
    )
}

# --- Originale Skript-Funktionen für den Runspace ---
$RunspaceFunctionsCode = @'

function Limit-LogText {
    if ($State.LogText.Length -gt 40000) {
        $State.LogText = "... [LOG GEKUERZT] ...`r`n" + $State.LogText.Substring($State.LogText.Length - 30000)
    }
}

function Write-Log ($Text) { 
    $State.LogText += "$Text`r`n"
    Limit-LogText
}

function Invoke-RobocopySafe ($Source, $Dest, $ExtraArgs) {
    if ($SyncHash.CancelRequested) { return 999 }

    $argString = "`"$Source`" `"$Dest`" $($ExtraArgs -join ' ')"
    Write-Log "Robocopy Befehl: robocopy $argString"

    $pInfo = New-Object System.Diagnostics.ProcessStartInfo
    $pInfo.FileName = "robocopy.exe"
    $pInfo.Arguments = $argString
    $pInfo.RedirectStandardOutput = $true
    $pInfo.RedirectStandardError  = $true
    $pInfo.UseShellExecute = $false
    $pInfo.CreateNoWindow  = $true
    $pInfo.StandardOutputEncoding = [System.Text.Encoding]::GetEncoding(850)

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pInfo
    $p.EnableRaisingEvents = $true

    # Gemeinsamer, thread-sicherer Zeilenpuffer
    $lineBag = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()

    $outHandler = Register-ObjectEvent -InputObject $p -EventName OutputDataReceived -Action {
        $line = $Event.SourceEventArgs.Data
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $Event.MessageData.Enqueue($line)
        }
    } -MessageData $lineBag

    $errHandler = Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -Action {
        $line = $Event.SourceEventArgs.Data
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $Event.MessageData.Enqueue("[STDERR] $line")
        }
    } -MessageData $lineBag

    $p.Start() | Out-Null
    $p.BeginOutputReadLine()
    $p.BeginErrorReadLine()

    [void]$SyncHash.ActiveProcesses.Add($p.Id)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    while (-not $p.HasExited) {
        if ($SyncHash.CancelRequested) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            break
        }

        if ($lineBag.Count -gt 0 -and $sw.ElapsedMilliseconds -gt 400) {
            $batch = [System.Text.StringBuilder]::new()
            $line  = $null
            while ($lineBag.TryDequeue([ref]$line)) {
                [void]$batch.AppendLine($line)
            }
            if ($batch.Length -gt 0) {
                $State.LogText += $batch.ToString()
                Limit-LogText
            }
            $sw.Restart()
        }

        Start-Sleep -Milliseconds 100
    }

    # Kurz warten damit asynchrone Events noch eintreffen koennen
    Start-Sleep -Milliseconds 300

    # Verbliebene Zeilen aus dem Puffer leeren
    $rest = [System.Text.StringBuilder]::new()
    $line = $null
    while ($lineBag.TryDequeue([ref]$line)) {
        [void]$rest.AppendLine($line)
    }
    if ($rest.Length -gt 0) {
        $State.LogText += $rest.ToString()
        Limit-LogText
    }

    Unregister-Event -SourceIdentifier $outHandler.Name -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $errHandler.Name -ErrorAction SilentlyContinue
    Remove-Job -Name $outHandler.Name -ErrorAction SilentlyContinue
    Remove-Job -Name $errHandler.Name -ErrorAction SilentlyContinue

    [void]$SyncHash.ActiveProcesses.Remove($p.Id)
    return $p.ExitCode
}

function Invoke-AppUpdateCheckAndInstall {
    param([string]$AppName, [string]$ExeName)
    if ($SyncHash.CancelRequested) { return }

    Write-Log "[INFO] Pruefe auf Updates fuer $AppName..."
    $installedVersion = $null
    $exePath = $null
    try {
        $appPathEntry = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$ExeName" -ErrorAction SilentlyContinue
        if ($appPathEntry) {
            $exePath = $appPathEntry.'(Default)'
            if (Test-Path $exePath) {
                $installedVersionString = (Get-Item $exePath).VersionInfo.ProductVersion
                $installedVersion = [version]($installedVersionString.Split(' ')[0])
                Write-Log "[INFO] Installierte $AppName Version: $installedVersion"
            }
        } else {
            Write-Log "[WARNUNG] $AppName scheint nicht installiert zu sein. Update-Pruefung uebersprungen."
            return 
        }
    } catch { return }

    $latestVersion = $null
    $productIdentifier = $AppName.ToLower()
    $apiUrl = "https://product-details.mozilla.org/1.0/${productIdentifier}_versions.json"
    try {
        $versionInfo = Invoke-RestMethod -Uri $apiUrl -UseBasicParsing
        if ($productIdentifier -eq "firefox") { $latestVersionString = $versionInfo.LATEST_FIREFOX_VERSION }
        else { $latestVersionString = $versionInfo.LATEST_THUNDERBIRD_VERSION }
        $latestVersion = [version]$latestVersionString
        Write-Log "[INFO] Neueste verfuegbare $AppName Version: $latestVersion"
    } catch { return }

    if ($latestVersion -gt $installedVersion) {
        Write-Log "[AKTION] Update fuer $AppName wird durchgefuehrt."
        $processName = $ExeName.Replace(".exe", "")
        Get-Process $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3

        $downloadUrl = "https://download.mozilla.org/?product=${productIdentifier}-latest-ssl&os=win64&lang=de"
        $installerPath = Join-Path $env:TEMP "${productIdentifier}-installer.exe"
        
        try {
            Write-Log "[INFO] Lade Installer herunter..."
            Invoke-WebRequest -Uri $downloadUrl -OutFile $installerPath -UseBasicParsing
            Write-Log "[INFO] Starte stille Installation..."
            
            $p = Start-Process -FilePath $installerPath -ArgumentList "-ms" -Wait:$false -PassThru
            [void]$SyncHash.ActiveProcesses.Add($p.Id)
            $p.WaitForExit()
            [void]$SyncHash.ActiveProcesses.Remove($p.Id)

            Write-Log "[ERFOLG] $AppName wurde erfolgreich aktualisiert."
        } catch {
            Write-Log "[FEHLER] Update von $AppName fehlgeschlagen."
        } finally {
            if (Test-Path $installerPath) { Remove-Item $installerPath -Force }
        }
    } else { Write-Log "[INFO] $AppName ist bereits aktuell." }
}

function Install-App {
    param ([string]$AppName, [string]$ExeName)
    if ($SyncHash.CancelRequested) { return }

    Write-Log "[INFO] Pruefe, ob $AppName installiert ist..."
    $appPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$ExeName"
    if (-not (Test-Path $appPath)) {
        if ($AppName -eq "Edge") { return }
        $wingetId = switch ($AppName) {
            "Firefox" { "Mozilla.Firefox.de" }
            "Thunderbird" { "Mozilla.Thunderbird.de" }
            "Chrome" { "Google.Chrome" }
            "Brave" { "Brave.Brave" }
            default { $null }
        }
        if ($wingetId) {
            Write-Log "[INFO] Versuche, $AppName via Winget zu installieren..."
            winget install --id $wingetId -e --accept-package-agreements --accept-source-agreements | Out-Null
            Write-Log "[INFO] Winget-Installation abgeschlossen."
        }
    } else { Write-Log "[INFO] $AppName ist bereits installiert." }
}

function Backup-UserProfile {
    $userDir = $State.SourcePath
    $destParentDir = $State.BackupPath
    $backupTargetDir = Join-Path $destParentDir "Benutzerprofil"

    if ($backupTargetDir.StartsWith($userDir, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Log "[FEHLER] Ziel liegt im Quellverzeichnis. Abbruch."
        return
    }

    $excludedDirs = @(
        (Join-Path $userDir "AppData"), (Join-Path $userDir "Anwendungsdaten"), (Join-Path $userDir "Application Data"),
        (Join-Path $userDir "Cookies"), (Join-Path $userDir "Links"), (Join-Path $userDir "Favorites"),
        (Join-Path $userDir "Local Settings"), (Join-Path $userDir "My Documents"), (Join-Path $userDir "NetHood"),
        (Join-Path $userDir "PrintHood"), (Join-Path $userDir "Recent"), (Join-Path $userDir "Templates"),
        (Join-Path $userDir "Start Menu"), (Join-Path $userDir "Druckumgebung"), (Join-Path $userDir "Netzwerkumgebung"),
        (Join-Path $userDir "SendTo"), (Join-Path $userDir "Vorlagen"), (Join-Path $userDir "Lokale Einstellungen"),
        (Join-Path $userDir "Eigene Dateien"), (Join-Path $userDir "Dropbox"), (Join-Path $userDir "HiDrive"),
        (Join-Path $userDir "Google Drive"), (Join-Path $userDir "iCloudDrive"), (Join-Path $userDir "AppData\Local\Temp"),
        (Join-Path $userDir "AppData\Local\Microsoft\Windows\INetCache"), (Join-Path $userDir "AppData\Local\Google\Chrome\User Data\Default\Cache"),
        (Join-Path $userDir "AppData\Local\BraveSoftware\Brave-Browser\User Data\Default\Cache")
    )
    $oneDriveFolders = Get-ChildItem -Path $userDir -Filter "OneDrive*" -Directory -ErrorAction SilentlyContinue
    if ($oneDriveFolders) { foreach ($folder in $oneDriveFolders) { $excludedDirs += $folder.FullName } }

    $robocopyArgs = @("/MIR", "/ZB", "/SL", "/R:0", "/W:0", "/MT:32", "/XJ", "/XA:SH")
    if ($SyncHash.FastMode) { $robocopyArgs += "/NP", "/NFL", "/NDL" }
    
    foreach ($exDir in $excludedDirs) {
        if (Test-Path $exDir) { $robocopyArgs += "/XD", "`"$exDir`"" }
    }

    Write-Log "[INFO] Starte Backup Benutzerprofil..."
    $exitCode = Invoke-RobocopySafe -Source $userDir -Dest $backupTargetDir -ExtraArgs $robocopyArgs
    if ($SyncHash.CancelRequested) { return }
    if ($exitCode -lt 8) { Write-Log "[ERFOLG] Benutzerprofil gesichert." } else { Write-Log "[FEHLER] Robocopy Fehlercode: $exitCode" }
}

function Restore-UserProfile {
    $destDir = $State.SourcePath
    $backupSourceDir = Join-Path $State.BackupPath "Benutzerprofil"
    if (-not (Test-Path $backupSourceDir)) { Write-Log "[FEHLER] Backup-Ordner nicht gefunden."; return }

    Write-Log "[INFO] Starte Restore Benutzerprofil..."
    $robocopyArgs = @("/E", "/ZB", "/COPYALL", "/R:1", "/W:1", "/MT:32")
    if ($SyncHash.FastMode) { $robocopyArgs += "/NP", "/NFL", "/NDL" }
    
    $exitCode = Invoke-RobocopySafe -Source $backupSourceDir -Dest $destDir -ExtraArgs $robocopyArgs
    if ($SyncHash.CancelRequested) { return }
    if ($exitCode -lt 8) { Write-Log "[ERFOLG] Benutzerprofil wiederhergestellt." } else { Write-Log "[FEHLER] Robocopy Fehlercode: $exitCode" }
}

function Backup-ApplicationProfile ($AppName, $ProfilePathInUserDir, $ProcessName, [string[]]$ExcludeDirs = @()) {
    if ($AppName -in "Firefox", "Thunderbird" -and $SyncHash.AutoUpdate) { Invoke-AppUpdateCheckAndInstall $AppName "$ProcessName.exe" }

    $appProfilePath = Join-Path $State.SourcePath $ProfilePathInUserDir
    if (Test-Path $appProfilePath) {
        Write-Log "[INFO] Beende $AppName..."
        Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        $targetBackupDir = Join-Path $State.BackupPath "$AppName-Profil"
        $roboArgs = @("/MIR", "/R:1", "/W:1", "/MT:32")
        if ($SyncHash.FastMode) { $roboArgs += "/NP", "/NFL", "/NDL" }
        # Cache-/Temp-Verzeichnisse ueberall im Profilbaum ausschliessen (Name-Match, ohne Pfad)
        foreach ($ex in $ExcludeDirs) { $roboArgs += "/XD", "`"$ex`"" }

        $exitCode = Invoke-RobocopySafe -Source $appProfilePath -Dest $targetBackupDir -ExtraArgs $roboArgs
        if ($SyncHash.CancelRequested) { return }
        if ($exitCode -lt 8) { Write-Log "[ERFOLG] $AppName Profil gesichert." } else { Write-Log "[FEHLER] ExitCode $exitCode" }
    } else { Write-Log "[FEHLER] Pfad nicht gefunden: $appProfilePath" }
}

function Restore-ApplicationProfile ($AppName, $ProfilePathInUserDir, $ProcessName) {
    Install-App $AppName "$ProcessName.exe"
    if ($AppName -in "Firefox", "Thunderbird" -and $SyncHash.AutoUpdate) { Invoke-AppUpdateCheckAndInstall $AppName "$ProcessName.exe" }

    $backupSourceDir = Join-Path $State.BackupPath "$AppName-Profil"
    if (-not (Test-Path $backupSourceDir)) { Write-Log "[FEHLER] $AppName Backup nicht gefunden."; return }
    $targetAppProfileDir = Join-Path $State.SourcePath $ProfilePathInUserDir

    Write-Log "[INFO] Beende $AppName..."
    Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    # Nicht-destruktiv: vorhandenes Profil umbenennen statt loeschen (Rollback moeglich)
    if (Test-Path $targetAppProfileDir) {
        $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $oldProfileDir = "${targetAppProfileDir}_alt_$stamp"
        try {
            Rename-Item -Path $targetAppProfileDir -NewName (Split-Path $oldProfileDir -Leaf) -ErrorAction Stop
            Write-Log "[INFO] Bisheriges $AppName-Profil gesichert nach: $oldProfileDir"
        } catch {
            Write-Log "[WARNUNG] Konnte altes Profil nicht umbenennen, ueberschreibe direkt."
            Remove-Item -Path $targetAppProfileDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    $parentOfTarget = Split-Path $targetAppProfileDir
    if (-not (Test-Path $parentOfTarget)) { New-Item -ItemType Directory -Path $parentOfTarget -Force -ErrorAction SilentlyContinue | Out-Null }

    $roboArgs = @("/MIR", "/R:1", "/W:1", "/MT:32")
    if ($SyncHash.FastMode) { $roboArgs += "/NP", "/NFL", "/NDL" }

    $exitCode = Invoke-RobocopySafe -Source $backupSourceDir -Dest $targetAppProfileDir -ExtraArgs $roboArgs
    if ($SyncHash.CancelRequested) { return }
    if ($exitCode -lt 8) { Write-Log "[ERFOLG] $AppName Profil wiederhergestellt." } else { Write-Log "[FEHLER] ExitCode $exitCode" }
}

function Export-WingetPackages {
    $wingetDir = Join-Path $State.BackupPath "Winget"
    if (-not (Test-Path $wingetDir)) { New-Item -ItemType Directory -Path $wingetDir -Force | Out-Null }
    $exportFile = Join-Path $wingetDir "Export.json"
    Write-Log "[INFO] Exportiere Programmliste..."
    winget export -o "`"$exportFile`"" --accept-source-agreements | Out-Null
    Write-Log "[ERFOLG] Winget Export beendet."
}

function Import-WingetPackages {
    $importFile = Join-Path $State.BackupPath "Winget\Export.json"
    if (-not (Test-Path $importFile)) { $importFile = Join-Path $State.BackupPath "Export.json" }
    if (Test-Path $importFile) {
        Write-Log "[INFO] Importiere Winget Liste (Bitte warten)..."
        winget import -i "`"$importFile`"" --accept-package-agreements --accept-source-agreements | Out-Null
        Write-Log "[ERFOLG] Winget Import beendet."
    } else { Write-Log "[FEHLER] Winget Exportdatei nicht gefunden." }
}

function Export-WlanProfiles {
    $destDir = Join-Path $State.BackupPath "WLAN-Profile"
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    Write-Log "[INFO] Exportiere WLAN Profile..."
    Write-Log "[WARNUNG] WLAN-Passwoerter werden im Klartext in den Backup-Ordner geschrieben. Sicher aufbewahren!"
    netsh wlan export profile folder="$destDir" key=clear | Out-Null
    Write-Log "[ERFOLG] WLAN Profile exportiert."
}

function Import-WlanProfiles {
    $srcDir = Join-Path $State.BackupPath "WLAN-Profile"
    if (Test-Path $srcDir) {
        Write-Log "[INFO] Importiere WLAN Profile..."
        Get-ChildItem -Path $srcDir -Filter "*.xml" | ForEach-Object {
            netsh wlan add profile filename="$($_.FullName)" | Out-Null
        }
        Write-Log "[ERFOLG] WLAN Profile importiert."
    } else { Write-Log "[FEHLER] WLAN Ordner nicht gefunden." }
}
'@

# Event für den "Ausführen" Button
$BtnExecute.Add_Click({
    $TaskList = @()
    if ($TgB_User.IsChecked)        { $TaskList += "1" }
    if ($TgB_Firefox.IsChecked)     { $TaskList += "2" }
    if ($TgB_Edge.IsChecked)        { $TaskList += "3" }
    if ($TgB_Chrome.IsChecked)      { $TaskList += "4" }
    if ($TgB_Brave.IsChecked)       { $TaskList += "5" }
    if ($TgB_Thunderbird.IsChecked) { $TaskList += "6" }
    if ($TgB_Winget.IsChecked)      { $TaskList += "7" }
    if ($TgB_Wlan.IsChecked)        { $TaskList += "8" }

    if ($TgR_User.IsChecked)        { $TaskList += "9" }
    if ($TgR_Firefox.IsChecked)     { $TaskList += "10" }
    if ($TgR_Edge.IsChecked)        { $TaskList += "11" }
    if ($TgR_Chrome.IsChecked)      { $TaskList += "12" }
    if ($TgR_Brave.IsChecked)       { $TaskList += "13" }
    if ($TgR_Thunderbird.IsChecked) { $TaskList += "14" }
    if ($TgR_Winget.IsChecked)      { $TaskList += "15" }
    if ($TgR_Wlan.IsChecked)        { $TaskList += "16" }

    if ($TaskList.Count -eq 0) {
        [System.Windows.MessageBox]::Show("Bitte waehle mindestens eine Aktion aus.", "Hinweis", [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information)
        return
    }

    if (-not $State.SourcePath -or -not $State.BackupPath) {
        [System.Windows.MessageBox]::Show("Bitte lege zuerst das Quell- und Zielverzeichnis fest.", "Fehler", [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Warning)
        return
    }

    $SyncHash.FastMode = -not $TgB_Logging.IsChecked
    $SyncHash.AutoUpdate = [bool]$TgB_AutoUpdate.IsChecked
    $SyncHash.TaskList = $TaskList
    $SyncHash.CancelRequested = $false

    # Flag bei Start explizit auf True setzen
    $SyncHash.LogAutoScroll = $true

    $State.UIEnabled = $false
    $State.LogText += "`n=========================================`n"
    $State.LogText += "Starte Abarbeitung der Warteschlange...`n"
    
    # Runspace starten
    Run-Async {
        . ([ScriptBlock]::Create($SyncHash.RunspaceFunctionsCode))

        # Cache-/Temp-Ordner, die in Chromium-Profilen NICHT mitgesichert werden (sparen GBs, irrelevant)
        $ChromiumCacheExcludes = @(
            "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache",
            "Service Worker", "Crashpad", "Application Cache", "Media Cache", "DawnGraphiteCache", "DawnWebGPUCache"
        )

        foreach ($choice in $SyncHash.TaskList) {
            if ($SyncHash.CancelRequested) { 
                Write-Log "`n[ABBRUCH] Vorgang durch Benutzer abgebrochen!"
                break 
            }

            Write-Log "`n--- Fuehre Aktion $choice aus ---"
            
            switch ($choice) {
                "1"  { Backup-UserProfile }
                "2"  { Backup-ApplicationProfile "Firefox" "AppData\Roaming\Mozilla\Firefox" "firefox" }
                "3"  { Backup-ApplicationProfile "Edge" "AppData\Local\Microsoft\Edge\User Data" "msedge" $ChromiumCacheExcludes }
                "4"  { Backup-ApplicationProfile "Chrome" "AppData\Local\Google\Chrome\User Data" "chrome" $ChromiumCacheExcludes }
                "5"  { Backup-ApplicationProfile "Brave" "AppData\Local\BraveSoftware\Brave-Browser\User Data" "brave" $ChromiumCacheExcludes }
                "6"  { Backup-ApplicationProfile "Thunderbird" "AppData\Roaming\Thunderbird" "thunderbird" }
                "7"  { Export-WingetPackages }
                "8"  { Export-WlanProfiles }
                
                "9"  { Restore-UserProfile }
                "10" { Restore-ApplicationProfile "Firefox" "AppData\Roaming\Mozilla\Firefox" "firefox" }
                "11" { Restore-ApplicationProfile "Edge" "AppData\Local\Microsoft\Edge\User Data" "msedge" }
                "12" { Restore-ApplicationProfile "Chrome" "AppData\Local\Google\Chrome\User Data" "chrome" }
                "13" { Restore-ApplicationProfile "Brave" "AppData\Local\BraveSoftware\Brave-Browser\User Data" "brave" }
                "14" { Restore-ApplicationProfile "Thunderbird" "AppData\Roaming\Thunderbird" "thunderbird" }
                "15" { Import-WingetPackages }
                "16" { Import-WlanProfiles }
            }
            Start-Sleep -Seconds 1
        }
        
        Write-Log "`n========================================="
        if ($SyncHash.CancelRequested) {
            Write-Log "Tool sicher beendet."
        } else {
            Write-Log "Alle ausgewaehlten Aktionen abgeschlossen!"
            $State.UIEnabled = $true
        }
    }
})

$SyncHash.RunspaceFunctionsCode = $RunspaceFunctionsCode
$SyncHash.UpdateCheckUrl  = $script:UpdateCheckUrl
$SyncHash.ProjectUrl      = $script:ProjectUrl
$SyncHash.CurrentVersion  = $script:VersionString
$SyncHash.UpdateBanner    = $UpdateBanner
$SyncHash.UpdateBannerText = $UpdateBannerText

Start-RunspaceTask $JobCleanupScript @([psobject]@{ Name = 'Jobs'; Variable = $Jobs })

$Window.Add_Closed({
    $SyncHash.CleanupJobs     = $false
    $SyncHash.CancelRequested = $true

    # Alle noch laufenden Kindprozesse (Robocopy, Installer usw.) abschuessen
    foreach ($procId in $SyncHash.ActiveProcesses.ToArray()) {
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
    }

    # Alle Runspaces stoppen und freigeben
    foreach ($Job in $Jobs.ToArray()) {
        try {
            $Job.PowerShell.Stop()
            $Job.PowerShell.Runspace.Close()
            $Job.PowerShell.Dispose()
        } catch {}
    }
    $Jobs.Clear()

    # Prozess sauber beenden (wichtig bei ps2exe-EXE)
    [Environment]::Exit(0)
})
$SyncHash.CleanupJobs = $true

# Fenster anzeigen
$Window.Add_Loaded({
    $Window.Activate()
    $Window.Focus()
    $Window.Topmost = $true
    $Window.Dispatcher.BeginInvoke([System.Windows.Threading.DispatcherPriority]::Background, [action]{ $Window.Topmost = $false }) | Out-Null

    # ======================================================================
    # 3. Den internen ScrollViewer fischen und überwachen
    # ======================================================================
    $sv = Find-ScrollViewer $TBLog
    if ($null -ne $sv) {
        $sv.Add_ScrollChanged({
            $e = if ($_.SourceEventArgs) { $_.SourceEventArgs } else { $_ }
            
            # Wenn sich die Ansicht ändert (z.B. durch Scrollen mit Rad oder Balken), 
            # aber KEIN neuer Text dazugekommen ist:
            if ($e.ExtentHeightChange -eq 0 -and $e.VerticalChange -ne 0) {
                # Toleranz von 5 Pixeln, um Ungenauigkeiten abzufangen
                $isAtBottom = (($e.VerticalOffset + $e.ViewportHeight) -ge ($e.ExtentHeight - 5))
                $SyncHash.LogAutoScroll = $isAtBottom
            }
        })
    }
    # ======================================================================

    # Update-Check im Hintergrund starten
    Start-RunspaceTask {
        . ([ScriptBlock]::Create($SyncHash.RunspaceFunctionsCode))
        try {
            $onlineVersionRaw = (Invoke-WebRequest -Uri $SyncHash.UpdateCheckUrl -UseBasicParsing -TimeoutSec 8 -ErrorAction Stop).Content.Trim()
            if ([version]$onlineVersionRaw -gt [version]$SyncHash.CurrentVersion) {
                $msg = "Neue Version $onlineVersionRaw verfuegbar (aktuell: $($SyncHash.CurrentVersion))"
                $SyncHash.Window.Dispatcher.Invoke([action]{
                    $SyncHash.UpdateBanner.Visibility = 'Visible'
                    $SyncHash.UpdateBannerText.Text = "Neue Version $onlineVersionRaw verfuegbar  (installiert: $($SyncHash.CurrentVersion))"
                })
                Write-Log "[UPDATE] $msg - Klicke auf das Banner oben rechts."
            }
        } catch {
            # Kein Internet oder Server nicht erreichbar -> stillschweigend ignorieren
        }
    } @(
        [psobject]@{ Name = 'DataContext'; Variable = $DataContext },
        [psobject]@{ Name = 'State';       Variable = $State       },
        [psobject]@{ Name = 'SyncHash';    Variable = $SyncHash    }
    )
})

$Window.ShowDialog()