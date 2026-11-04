# 用法: powershell -File make-ico.ps1 -Png <png> -Ico <ico> [-Sizes 16,24,32,48,64,128,256]
# 将 PNG 转为多尺寸 ICO（条目采用规范 BMP(32bpp + AND mask) 编码，兼容性最好）。
param(
  [Parameter(Mandatory=$true)][string]$Png,
  [Parameter(Mandatory=$true)][string]$Ico,
  [int[]]$Sizes = @(16,24,32,48,64,128,256)
)
Add-Type -AssemblyName System.Drawing

function New-BmpEntry([System.Drawing.Bitmap]$bmp) {
  $s = $bmp.Width
  $rect = New-Object System.Drawing.Rectangle(0,0,$s,$s)
  $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $data.Stride
  $pix = New-Object byte[] ($s*$s*4)
  $ptr = $data.Scan0
  for ($row = 0; $row -lt $s; $row++) {
    $src = [IntPtr]::Add($ptr, $row*$stride)
    $dst = ($s-1-$row)*$s*4   # BMP 行序自底向上
    [System.Runtime.InteropServices.Marshal]::Copy($src, $pix, $dst, $s*4)
  }
  $bmp.UnlockBits($data)
  $maskStride = [int]([Math]::Ceiling([Math]::Ceiling($s/8)/4)*4)  # AND mask 行按 4 字节对齐
  $mask = New-Object byte[] ($maskStride*$s)
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([UInt32]40)                 # biSize
  $bw.Write([Int32]$s)                  # biWidth
  $bw.Write([Int32]($s*2))              # biHeight = 图高*2（含 XOR+AND）
  $bw.Write([UInt16]1)                  # biPlanes
  $bw.Write([UInt16]32)                 # biBitCount
  $bw.Write([UInt32]0)                  # biCompression = BI_RGB
  $bw.Write([UInt32]($s*$s*4))          # biSizeImage
  $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
  $bw.Write($pix)
  $bw.Write($mask)
  $bw.Flush()
  $bytes = $ms.ToArray()
  $bw.Close(); $ms.Close()
  return ,$bytes
}

$src = [System.Drawing.Image]::FromFile($Png)
$blobs = @(); $sizesUsed = @()
foreach ($s in $Sizes) {
  $bmp = New-Object System.Drawing.Bitmap($s,$s)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($src, 0, 0, $s, $s)
  $g.Dispose()
  $blobs += ,(New-BmpEntry $bmp)
  $sizesUsed += $s
  $bmp.Dispose()
}
$src.Dispose()

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$blobs.Count)  # ICONDIR
$offset = 6 + 16*$blobs.Count
for ($i = 0; $i -lt $blobs.Count; $i++) {
  $s = $sizesUsed[$i]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
  $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$blobs[$i].Length)
  $bw.Write([UInt32]$offset)
  $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Ico, $ms.ToArray())
$bw.Close(); $ms.Close()
Write-Host ("OK: {0} ({1} bytes, sizes: {2})" -f $Ico, (Get-Item $Ico).Length, ($sizesUsed -join ','))
