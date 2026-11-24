---
title: "Bt601FullRange"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/Bt601FullRange.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["Bt601FullRange"]
lang: "zh-CN"
---
# Bt601FullRange

源码: [`Bt601FullRange.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/Bt601FullRange.cs)

BT.601 全范围（JFIF）YUV→RGB 转换系数——全仓 CPU 转换路径的唯一常量真源。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public static class Bt601FullRange
```

## 备注

公式（Y/U/V 为 0..255 整数域，色度中心 128）：
R = Y + `Rv`·(V−128)；
G = Y − `Gu`·(U−128) − `Gv`·(V−128)；
B = Y + `Bu`·(U−128)；结果钳制到 0..255。

归一化域（0..1，色度中心 0.5）使用同一组系数。GPU 着色器路径
（D3D11 HLSL / GLSL / MSL / SPIR-V）内嵌同一组数值，修改本类时须同步核对各着色器源，
否则 CPU 兜底与 GPU 主路径色彩漂移。

本类只覆盖 BT.601-Full 默认矩阵；BT.709/BT.2020 与 limited-range 补偿系数
由消费方按 `VideoColorInfo` 另行选择，不在本类范围。

## 成员

### 402

```csharp
public const double Rv = 1.402;
```

R 通道 V 系数（+1.402）。

### 344136

```csharp
public const double Gu = 0.344136;
```

G 通道 U 系数（−0.344136，公式中取负）。

### 714136

```csharp
public const double Gv = 0.714136;
```

G 通道 V 系数（−0.714136，公式中取负）。

### 772

```csharp
public const double Bu = 1.772;
```

B 通道 U 系数（+1.772）。

### 128

```csharp
public const int ChromaCenter = 128;
```

色度中心（8-bit 整数域，128）。
