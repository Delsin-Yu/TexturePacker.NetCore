# TexturePacker.NetCore

A .Net Library that offers texture packing functionality.

This project is based on [mfascia's TexturePacker](https://github.com/mfascia/TexturePacker).
This project leverages the `SixLabors.ImageSharp` library for image processing.

## Guide

```csharp
var (atlasInfo, log, errorLog) = await TexturePacker.PackAsync(["myImage.png"], "myPackedImageDir", "*.png", 1024);
```
