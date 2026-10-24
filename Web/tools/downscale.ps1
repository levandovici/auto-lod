Add-Type -AssemblyName System.Drawing
$src = 'C:\Users\nichi\Downloads\ship_pinnace_4k.fbx\textures'
$dst = 'C:\Users\nichi\source\auto-lod\Web\public\lods'
Get-ChildItem $src -Filter '*_diff_4k.png' | ForEach-Object {
  $img = [System.Drawing.Image]::FromFile($_.FullName)
  $w = 1024; $h = [int]($img.Height * $w / $img.Width)
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.DrawImage($img, 0, 0, $w, $h)
  $out = Join-Path $dst (($_.BaseName -replace '_4k$', '') + '.jpg')
  $enc = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
  $ep = New-Object System.Drawing.Imaging.EncoderParameters 1
  $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]85)
  $bmp.Save($out, $enc, $ep)
  $g.Dispose(); $bmp.Dispose(); $img.Dispose()
  Write-Output (Split-Path $out -Leaf)
}
