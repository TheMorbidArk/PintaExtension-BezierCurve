# 形状对象

在 **MS Paint** 行为下，完成的曲线不会立刻变成像素：它会保留为一个带选择框的对象，直到
点击框外。这篇笔记解释背后的模型，以及为什么这样设计。

相关：[gtk-gotchas.md](gtk-gotchas.md) 讲框的控件，README 讲用户看到的东西。

---

## 对象是什么

`ShapeObject` 只存三样东西，别的都没有：

| 部分 | 类型 | 含义 |
|---|---|---|
| `local` | `BezierCurve` | **自身**坐标系里的曲线 |
| `angle` | `double` | 转了多少，弧度 |
| `offset` | `PointD` | 它的中心在画布上的位置 |

其他一切都是推导出来的：

```
centre  = Extent(local) 的中点 + offset
Map(p)  = 把 p 绕 Extent(local) 的中点旋转 angle，再加 offset
Curve   = 对 local 的四个点应用 Map
Bounds  = CurveGeometry.Bounds (Curve, 笔宽)
```

因为旋转是绕本地曲线的中点，形状转动时中心不动——这让旋转手柄像转盘，而不是绕旁边某个
支点转。

## 为什么不用一个累乘矩阵

第一版存了一个 `Matrix3x2`，每次移动都往里面乘一个平移。移动没问题，0.6.0 够用，但形状
一旦能旋转就崩了：

* **缩放。** 矩阵分不清“形状自己的 x 轴”和“画布的 x 轴”。拖一个转了 90 度的形状的右边，
  会在屏幕上横向拉伸它，而不是沿它自己的边。
* **框。** 轴对齐的矩形框不住转过的形状，手柄会落在错误矩形的角上。

把曲线放在自己的坐标系里，这两件事就自然解决了：缩放乘 `local` 坐标，框就是本地范围映射
到画布上。

## 操作

| 操作 | 改什么 |
|---|---|
| `Translate (dx, dy)` | `offset` |
| `RotateBy (radians)` | `angle`——中心按构造保持不动 |
| `ScaleLocal (sx, sy, localAnchor)` | `local`，再改 `offset` 让锚点不动 |
| `FlipHorizontal / Vertical` | `local`，绕自身中点镜像 |

### `ScaleLocal` 让画布上的一个点保持不动

被拖的手柄应该让**对角**保持不动。本地点缩放之后，本地范围的中点可能移动了，那会把整个
形状一起拖走。所以锚点的画布位置在缩放前后各取一次，差值加进 `offset`：

```csharp
PointD before = Map (localAnchor);

local = scaled points;

PointD after = Map (localAnchor);
offset += before - after;
```

这就是冒烟测试能断言“手柄对角在半个像素内不动”的原因。

### 缩放不会把形状压没

`ScaleLocal` 把两个因子都夹到 `MINIMUM_SCALE`，并拒绝 `NaN` / 无穷大。缩到零的形状没有
框可抓，把手柄拖过锚点会把它翻里朝外，那不是手柄该有的意思。

## 漂移可以避免：快照，不要累加

每次拖动在鼠标按下时保存一个 `ShapeState`（`local`、`angle`、`offset`），每次鼠标移动先
恢复它，再应用按下以来的**总**位移：

```csharp
shape_object.Restore (shape_drag_state);
shape_object.Translate (total_delta_x, total_delta_y);
```

如果改成每次鼠标移动应用*增量*，长时间拖动会累积浮点误差，缩放还会把因子复合起来。

## 框

`ShapeFrame` 是虚线轮廓、八个缩放手柄和圆形旋转手柄。它们是 `IToolHandle`，所以画在窗口
空间，任何缩放下尺寸不变。

* 轮廓是一个**四边形**：本地范围的四个角映射到画布。它跟着形状转。
* `ContainsInside` 是点在四边形内的测试，不是矩形测试，所以在转过的形状内部拖动是有效的。
* 旋转手柄在上边中点上方，沿形状朝向偏移，小杆用框的颜色。

## 工具这一侧

`CurveTool` 拥有对象和历史：

* **`BeginShapeObject`** 把刚画好的曲线变成对象。曲线还在工具图层上，所以像素还没被动过。
* **`CommitShapeObject`** 把对象画到图层上，并为它经历的一切——移动、缩放、旋转、翻转
  ——只压入**一个** `SimpleHistoryItem`。撤销快照是画曲线之前的图层。
* **`ClearShapeObject`** 不碰图层，直接忘掉它，用于 Escape。

对象存在时它*不在历史里*，所以撤销必须小心：

```csharp
bool was_in_progress = HasUncommittedWork;   // 正在画的曲线，或一个对象
ResetShape (document);
if (was_in_progress)
    return true;                              // 已处理：不要回退文档
```

没有这段，一次 Ctrl+Z 会把对象丢掉，**并且**回退在它之前落下的曲线。
