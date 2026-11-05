# GTK4 踩坑记录

样式面板和工具选项栏花了四个版本才做对。下面每一条都是曾经发布出去的 bug，所以连同
暴露它的症状一起记下来。

相关：[mono-addins-gotchas.md](mono-addins-gotchas.md) 讲插件本身的加载和更新。

---

## 1. `GtkSwitch.activate` 不携带新状态

**症状：** 在样式面板里点开关，开关动了，但什么都没变。

`activate` 只负责启动开关动画。GTK 文档说得很直白：

> 用于给开关做动画。应用不应该连接这个信号，而应该使用 `GtkSwitch:active` 属性。

所以在 `activate` 处理函数里读 `switch.Active` 拿到的是**旧**值。

```csharp
// 错：读到的是点击生效之前的状态
toggle.OnActivate += (_, _) => on_change (toggle.Active);

// 对：监听属性，不管是什么让它变的
toggle.OnNotify += (_, _) => on_change (toggle.Active);
```

`OnNotify` 在代码自己设置 `Active` 时也会触发，所以任何写这个属性的地方都要准备好被
回调。这就是面板里的 `Select` 辅助方法要防重入的原因。

## 2. `Gtk.ToggleButton` 的 "clicked" 在状态改变**之前**触发

**症状：** 一组按钮什么都不做，按钮也保持未按下。

和上面同一类错误：`clicked` 到达时，控件报告的仍是旧的 `Active`。用 `toggled`，它在
控件处理完点击之后才发出。

## 3. 弹出菜单里的下拉框会抢走 grab

**症状：** 在样式面板里打开下拉框后，点外面不再关闭面板——只有 Esc 有用。

`GtkComboBox` 会打开自己的弹出菜单。那个弹出菜单从面板手里拿走指针 grab 且不还回来
（[GNOME/gtk#4529]）。面板从此再也看不到外面的点击。

修法是干脆不打开窗口：面板改用一排行为像单选按钮的 `GtkToggleButton`
（`OptionGroup`）。

**经验法则：弹出菜单里不能放任何会自己打开弹出菜单的东西。**

## 4. 控件会被拉伸填满网格单元

**症状：** 面板里的开关变成 250px 宽的椭圆，数字框变得巨大。

`Gtk.Grid` 会把整列宽度给每个子控件。任何想保持自然尺寸的东西都必须明说：

```csharp
toggle.SetHalign (Gtk.Align.Start);
toggle.SetValign (Gtk.Align.Center);
spin.SetWidthChars (3);       // 小框还要设 Halign
```

一组按钮也一样：不设 `Halign = Start` 它们就会散开。

## 5. 枚举值不是下拉框索引

**症状：** “圆头”和“平头”对调了，三种连接方式也全对调了。

`Cairo.LineCap` 是 `Butt = 0, Round = 1, Square = 2`，但面板列出的顺序是圆、平、方。
把索引直接强转成枚举会静默映射到错误的值。

```csharp
// 错
stroke_cap = (LineCap) combo.Active;

// 对：面板显示的顺序只写一次
private static readonly LineCap[] CAPS = [LineCap.Round, LineCap.Butt, LineCap.Square];
stroke_cap = CAPS[combo.Active];
```

## 6. 弹出菜单弹出前需要父控件

`Gtk.Popover.New ()` 不够：不调用 `SetParent (widget)`，弹出菜单没有可锚定的东西。
Pinta 自己的 `PopoverMenu` 用法就是这个模式：

```csharp
popover.SetParent (button);
button.OnClicked += (_, _) => popover.Popup ();
```

## 7. 隐藏一组控件意味着连分隔线一起隐藏

工具选项栏是一行扁平的控件。只隐藏按钮会把它们周围的分隔线留成孤立的线，所以每个可选
组都装进一个 `Gtk.Box`，切换的是 box 的 `Visible`。

---

## 无屏幕测试 UI

上面每一条都是被断言抓到的，不是靠看窗口发现的。有效的模式：

* 把控件放在字段里，提供 `internal` 访问器；
* 在冒烟测试里像点击一样驱动它（`switch.Active = true`、`group.Press (1)`、
  `combo.Active = 1`）；
* 断言工具自己的状态，而不是像素。

`scripts/smoke-test.sh` 在 `xvfb-run` 下跑整套东西，所以抛异常的控件会变成失败，而不是
一个空白窗口。

[GNOME/gtk#4529]: https://gitlab.gnome.org/GNOME/gtk/-/work_items/4529
