# Mono.Addins / Pinta 插件踩坑记录

下面每一条都是移植 KolourPaint 曲线工具时踩出来的。每一条都花掉一个版本，所以连同
证明它的证据一起记下来。

相关：[packaging.md](packaging.md) 讲为什么插件必须是单个程序集。

---

## Pinta 如何加载这个插件

```csharp
// Pinta/MainWindow.Activate()
string addins_dir = Path.Combine (PintaCore.Settings.GetUserSettingsDirectory (), "addins");
AddinManager.Initialize (addins_dir);
AddinManager.Registry.Update ();
AddinManager.AddExtensionNodeHandler (typeof (IExtension), OnExtensionChanged);
```

* `addins_dir` 是 Mono.Addins 的**注册表**目录，不是扫描目录。安装的插件最终在
  `<addins_dir>/addins/<id>.<version>/`，通过注册表数据库（`addin-db-004`）找到，而不是
  靠扫描。
* 推论：**把 DLL 复制到 `~/.config/Pinta/addins/` 什么也不会发生。** 用插件对话框（或
  `scripts/dev-install.sh`，它把 DLL 放到 Pinta 可执行文件旁边——那是唯一*会*被扫描的
  地方）。
* 扩展点是 Pinta 的 `IExtension` 接口；节点的路径形如
  `/Pinta/TypeExtensions/Pinta.Core.IExtension/<node-id>`。

## 1. 引用扩展类型时*不要*带程序集版本

Mono.Addins 通常会把完整的程序集标识写进节点：

```xml
<Type type="TheMorbidArk.PintaExtension.BezierCurve.CurveAddin, TheMorbidArk.PintaExtension.BezierCurve, Version=0.4.2.0, Culture=neutral, PublicKeyToken=null" />
```

这样的节点只能由*那个确切的文件*满足。覆盖更新之后，过期节点要的是旧标识，Mono.Addins
的 `AssemblyResolve` 处理器尝试加载新文件，.NET 拒绝
（`FileLoadException: ... manifest definition does not match` 或
`Assembly with same name is already loaded`）。Pinta 会报“该插件可能与此版本的 Pinta
不兼容”。

手工声明节点、类型不带版本就能修好——运行时会实例化已经加载的那个
`TheMorbidArk.PintaExtension.BezierCurve`：

```xml
<Extension path="/Pinta/TypeExtensions/Pinta.Core.IExtension">
	<Type id="CurveAddin" type="TheMorbidArk.PintaExtension.BezierCurve.CurveAddin, TheMorbidArk.PintaExtension.BezierCurve" />
</Extension>
```

注意显式的 `id`：没有它，不带版本的类型会让 Mono.Addins 编造一个 id（`__nid_1`），
而不是从类型名推导。

## 2. 保持程序集标识稳定

`AssemblyVersion` 故意冻结在 `1.0.0.0`（见 `TheMorbidArk.PintaExtension.BezierCurve.csproj`）。
Pinta 按标识解析扩展类型，所以每个版本换标识正是把更新变成 `FileLoadException` 的原因。
发布号放在 `.addin.xml`、`FileVersion` 和 `InformationalVersion` 里——Mono.Addins 判断
有没有更新，看的是*插件*版本，从来不是程序集版本。

任何必须随版本变化、又从“版本”推导出来的东西，都必须读清单，而不是
`Assembly.GetName().Version`——比如图标缓存目录，它用的是 `CurveAddin.AddinVersion()`。

## 3. 覆盖更新在同一个会话里卸载再加载

在运行中的 Pinta 上安装，会在同一轮事件里对旧节点发 remove、对新节点发 add。由此有两条
规则：

* **不要在 `Uninitialize` 里急着拆。** 在那里移除工具会让工具箱空到 Pinta 重启。
  `CurveAddin.Uninitialize` 等 250 ms，问注册表插件是否还装着，更新时保留工具；真正的
  卸载或禁用仍然会移除它。
* **让 `Initialize` 幂等。** 新实例初始化时旧实例可能还活着；天真的 `AddTool` 会抛
  “attempted to add a duplicate tool”。

因为 .NET 不能卸载程序集，运行中的会话会一直用*旧代码*到下次启动。这是插件能做到的
最好情况——工具保持可用，新代码在重启后生效。

## 4. 卸载已加载的插件是计划任务

Mono.Addins 不能删除进程已加载的程序集，所以卸载会被记录下来，在下次启动时执行。这
*只有*在没留下别的东西时才没问题；这次卸载 bug 见 [packaging.md](packaging.md)。

## 5. `same id + same version` 等于“已安装”

注册表用插件 id *和*版本来判断更新。忘了改 `.addin.xml` 里的 `version`，安装看起来就像
什么都没做。`ManifestTests` 同时守住这一点和冻结的程序集标识，`scripts/check-version.sh`
拒绝为已经有 tag 的版本打包。

### 第二次安装同一版本

改版本号不只是为了发版。在 Pinta 运行时再次安装**同一**版本，会让 Mono.Addins 卸载插件
然后*什么都不加载*，于是：

* 工具从工具箱消失；
* 插件被留在“已计划卸载”状态，之后每次安装都失败，直到重启 Pinta：

  > The addin Bezier Curve is scheduled for uninstallation. Please restart the
  > application before trying to re-install it.
  >
  > （插件 Bezier Curve 已计划卸载。请重启应用后再尝试重新安装。）

这在插件内部无法避免。`Uninitialize` 运行时，卸载和重装同一版本是**无法区分**的——
在 Pinta 3.1.2 上实测：

| 那一刻检查 | 卸载 | 重装（同版本） |
|---|---|---|
| `Registry.GetAddin (id)` | null | null |
| 程序集文件存在 | 是 | 是 |
| 插件目录存在 | 是 | 是 |
| 新的 `Initialize` 到达 | 否 | 否 |
| `Registry.GetAddins ().Length` | 3 | 3 |

“以防万一”移除工具会清空工具箱；“以防万一”保留工具会在真正卸载后留下一个工具。插件
保留 0.5.1 验证过的行为——移除工具——README 让用户重启。

**升级到更新版本不受影响**：新实例在同一轮事件里初始化，工具保留。

## 6. 错误诊断的代价更大

0.5.6 发布过一个“清理”：启动时删除旧版本的目录，理由是残留的
`TheMorbidArk.PintaExtension.BezierCurve.<old>/TheMorbidArk.PintaExtension.BezierCurve.dll`
会让 Mono.Addins 加载两次程序集并以 `Assembly with same name is already loaded` 失败。

这个理论是错的。事后实测：两个版本目录并排**不会**破坏启动。改动在 0.5.7 回退，而它
在此期间让事情更糟——清理可能删掉正在运行的插件所加载的目录。

引发这一切的堆栈指向 `/Users/runner/work/1/s/Mono.Addins/...`——那是构建 Mono.Addins 的
*构建机*路径，烙在它的 PDB 里，不是看到错误的用户的平台。

## 7. 图标来自 GTK 图标主题

`ToolBoxButton` 用 `IconName`，所以图标是在 GTK 图标主题里查的——只把资源嵌进 DLL 不够。
插件把 `hicolor/<size>/actions/` 树作为嵌入资源发布，解包到
`~/.cache/pinta-extension/bezier-curve/<addin-version>/icons`，然后调用
`GtkExtensions.GetDefaultIconTheme().AddSearchPath(directory)`。

还要小心 SVG 嗅探：gdk-pixbuf 只检查前 ~256 字节，所以把 `<svg>` 元素放在文件靠前的位置
（在它前面放长注释会让图标静默加载失败）。

## 8. 有用的 Pinta API 笔记

* `IToolHandle` 画在**窗口空间**（`PintaCanvas.DrawHandles`），并裁剪到画布视图——任何
  缩放下尺寸不变。用 `InvalidateWindowRect` 重绘。
* `document.Layers.ToolLayer` + `CreateClippedToolContext()` 是预览用的临时图层；
  `EditActions` 会在若干操作里清掉它，所以工具必须能重建自己的预览。
* `SimpleHistoryItem(icon, name, undoSurface, layerIndex)` 对绘画型工具来说足够做撤销。
* `PintaCore.Chrome.SetStatusBarText` 在具体的 `ChromeManager` 上，不在 `IChromeService`
  上——能用，但略非正式。
* Pinta 的形状引擎（`ShapeEngine` / `BaseEditEngine`）**不能**从插件扩展：
  `GeneratedPoints` 是 `private protected`，样式 setter 是 `internal`，`ShapeTypes` 是
  封闭枚举，和内置工具一一映射。见 [feasibility.md](feasibility.md)。
* `Pinta.Tools.Handles.RectangleHandle` 是八个缩放手柄加标准方向光标的可用先例，但它在
  `Pinta.Tools` 程序集里，插件不引用它。可以当模板读，不能复用。
* `Pinta.Resources.StandardCursors` 可以通过 `Pinta.Core` 拿到，有 `Move`（四向箭头）、
  `Grab`/`Grabbing` 和 `ResizeN/E/S/W/NE/NW/SE/SW`。没有旋转光标；插件给圆形旋转手柄用
  `Grab`。
* `ImageActions.FlipHorizontal` 之类会先调 `tools.Commit ()` 再翻转整层，所以没法让它们
  作用于工具的对象。插件无法从 `Command.Activated` 移除 Pinta 的处理器，而
  `ChromeManager` 只暴露 `AdjustmentsMenu` 和 `EffectsMenu`——Image 菜单根本够不到。

---

## 不点 UI 怎么验证更新

`scripts/package-test.sh` 在一次性配置目录里做完这些：

```
1. 构建 dist/<package>.mpack
2. 启动一次 Pinta 让它建注册表，通过 Pinta 自己的注册表安装包（和插件对话框同一个调用）
3. 重启，插件不在 Pinta 的 bin 目录里，断言它从注册表加载、工具能用（完整冒烟套件）
4. 构建*下一个*版本（改清单），在 Pinta 运行时安装，断言工具仍然注册、仍然能画
5. 通过注册表卸载，断言工具消失
6. 重启，断言没有残留文件、没有过期注册表项、没有 FileLoadException
```

手工复现从旧版本升级：

```bash
git worktree add /tmp/old v0.4.2
dotnet build /tmp/old/src/TheMorbidArk.PintaExtension.BezierCurve/TheMorbidArk.PintaExtension.BezierCurve.csproj -c Release \
    -p:PintaSourceDir="$PWD/.pinta/Pinta"
dotnet tools/mpack/bin/Debug/net8.0/mpack.dll build /tmp/pkgs /tmp/old/src/TheMorbidArk.PintaExtension.BezierCurve/bin/Release/net8.0/TheMorbidArk.PintaExtension.BezierCurve.dll
# 安装那个包，重启 Pinta，然后在它运行时安装当前包
```

冒烟测试插件会报告 `tool after install: True/False`，可以用
`KP_CURVE_TEST_EXPECT_TOOL=1` / `KP_CURVE_TEST_DRAW_AFTER_INSTALL=1` 断言，这正是
`package-test.sh` 第 4 步用的。
