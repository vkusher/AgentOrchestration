#requires -Version 5
# Generate sample PDF + PNG mortgage documents from the existing .txt files.
# PNG via System.Drawing; PDF is a hand-rolled minimal single-page PDF 1.4.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '..\samples\mortgage-docs' | Resolve-Path).Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-PngFromText {
    param(
        [Parameter(Mandatory)] [string]$Text,
        [Parameter(Mandatory)] [string]$OutPath,
        [int]$Width = 900,
        [int]$Height = 600,
        [string]$Title = ''
    )
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::FromArgb(245, 245, 240))

    $border = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 60, 80), 4)
    $g.DrawRectangle($border, 10, 10, $Width - 20, $Height - 20)

    if ($Title) {
        $titleFont  = New-Object System.Drawing.Font('Arial', 22, [System.Drawing.FontStyle]::Bold)
        $titleBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(20, 40, 90))
        $g.DrawString($Title, $titleFont, $titleBrush, 30, 25)
        $titleFont.Dispose(); $titleBrush.Dispose()
    }

    $bodyFont  = New-Object System.Drawing.Font('Consolas', 13)
    $bodyBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::Black)
    $y = 70
    foreach ($line in ($Text -split "`n")) {
        if ($y -gt ($Height - 30)) { break }
        $g.DrawString($line.TrimEnd(), $bodyFont, $bodyBrush, 30, $y)
        $y += 22
    }
    $bodyFont.Dispose(); $bodyBrush.Dispose()
    $border.Dispose(); $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "PNG written: $OutPath ($([Math]::Round((Get-Item $OutPath).Length / 1KB, 1)) KB)"
}

function ConvertTo-PdfText {
    param([string]$Value)
    return $Value.Replace('\', '\\').Replace('(', '\(').Replace(')', '\)')
}

function New-PdfFromText {
    param(
        [Parameter(Mandatory)] [string]$Text,
        [Parameter(Mandatory)] [string]$OutPath,
        [string]$Title = ''
    )
    $lines = New-Object 'System.Collections.Generic.List[string]'
    if ($Title) { $lines.Add("TITLE: $Title"); $lines.Add('') }
    foreach ($l in ($Text -split "`n")) { $lines.Add($l.TrimEnd()) }

    $stream = New-Object System.Text.StringBuilder
    [void]$stream.AppendLine('BT')
    [void]$stream.AppendLine('/F1 11 Tf')
    [void]$stream.AppendLine('13 TL')
    [void]$stream.AppendLine('50 780 Td')
    $first = $true
    foreach ($l in $lines) {
        $safe = ConvertTo-PdfText $l
        if ($first) {
            [void]$stream.AppendLine("($safe) Tj")
            $first = $false
        } else {
            [void]$stream.AppendLine('T*')
            [void]$stream.AppendLine("($safe) Tj")
        }
    }
    [void]$stream.AppendLine('ET')
    $contentBytes = [Text.Encoding]::ASCII.GetBytes($stream.ToString())

    $buf = New-Object 'System.Collections.Generic.List[byte]'
    $buf.AddRange([Text.Encoding]::ASCII.GetBytes("%PDF-1.4`n%aabb`n"))

    $offs = New-Object 'System.Collections.Generic.List[int]'

    function Write-Obj {
        param(
            [System.Collections.Generic.List[byte]]$Buf,
            [System.Collections.Generic.List[int]]$Offs,
            [int]$Num,
            [string]$Body
        )
        $Offs.Add($Buf.Count)
        $b = [Text.Encoding]::ASCII.GetBytes("$Num 0 obj`n$Body`nendobj`n")
        $Buf.AddRange($b)
    }

    Write-Obj $buf $offs 1 '<< /Type /Catalog /Pages 2 0 R >>'
    Write-Obj $buf $offs 2 '<< /Type /Pages /Count 1 /Kids [3 0 R] >>'
    Write-Obj $buf $offs 3 '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>'

    # Object 4: content stream — hand-written so we can embed binary length.
    $offs.Add($buf.Count)
    $buf.AddRange([Text.Encoding]::ASCII.GetBytes("4 0 obj`n<< /Length $($contentBytes.Length) >>`nstream`n"))
    $buf.AddRange($contentBytes)
    $buf.AddRange([Text.Encoding]::ASCII.GetBytes("`nendstream`nendobj`n"))

    Write-Obj $buf $offs 5 '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'

    $xrefStart = $buf.Count
    $xref = New-Object System.Text.StringBuilder
    [void]$xref.AppendLine('xref')
    [void]$xref.AppendLine('0 6')
    [void]$xref.AppendLine('0000000000 65535 f ')
    foreach ($o in $offs) { [void]$xref.AppendLine(('{0:D10} 00000 n ' -f $o)) }
    [void]$xref.AppendLine('trailer')
    [void]$xref.AppendLine('<< /Size 6 /Root 1 0 R >>')
    [void]$xref.AppendLine('startxref')
    [void]$xref.AppendLine("$xrefStart")
    [void]$xref.AppendLine('%%EOF')
    $buf.AddRange([Text.Encoding]::ASCII.GetBytes($xref.ToString()))

    [IO.File]::WriteAllBytes($OutPath, $buf.ToArray())
    Write-Host "PDF written: $OutPath ($([Math]::Round((Get-Item $OutPath).Length / 1KB, 1)) KB)"
}

# --- Generate PNGs ---
$idText  = Get-Content (Join-Path $Root 'government_id_front.txt')  -Raw
New-PngFromText -Text $idText  -OutPath (Join-Path $Root 'government_id_front.png') -Title 'STATE OF ISRAEL - IDENTITY CARD' -Width 900 -Height 580

$paySrc  = Get-Content (Join-Path $Root 'payslip_march_2026.txt') -Raw
New-PngFromText -Text $paySrc  -OutPath (Join-Path $Root 'payslip_march_2026.png')  -Title 'PAYSLIP - MARCH 2026' -Width 900 -Height 660

# --- Generate PDFs ---
$bankSrc = Get-Content (Join-Path $Root 'bank_statement_april_2026.txt') -Raw
New-PdfFromText -Text $bankSrc -OutPath (Join-Path $Root 'bank_statement_april_2026.pdf') -Title 'BANK STATEMENT - APRIL 2026'

$apSrc   = Get-Content (Join-Path $Root 'property_appraisal.txt') -Raw
New-PdfFromText -Text $apSrc   -OutPath (Join-Path $Root 'property_appraisal.pdf') -Title 'PROPERTY APPRAISAL REPORT'

Write-Host '---'
Get-ChildItem $Root | Where-Object { $_.Extension -in '.png','.pdf' } | Select-Object Name, Length | Format-Table -AutoSize
