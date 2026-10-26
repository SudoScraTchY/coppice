---
name: avalonia
description: Use when working with Avalonia UI framework — building, styling, binding, animating, or deploying Avalonia apps with .NET.
---

# Avalonia UI

## Overview
Avalonia is a cross-platform .NET UI framework using XAML (`.axaml`), with its own Skia-based rendering engine, supporting Windows, macOS, Linux, iOS, Android, and WASM.

**Supported versions:** 11.3 (legacy), 12.0, **12.1 (current stable, 12.1.4)**. Default for new projects: latest 12.1.x on `net10.0`.

**Rule 1 — version first:** Before writing or reviewing code, detect the project's Avalonia version (`Avalonia` `PackageReference` in `*.csproj` / `Directory.Packages.props`). Many APIs differ between 11.x and 12.x (clipboard, drag-drop, Android lifetime, `Watermark`, window decorations, compiled-binding default). Load `avalonia-versions` when unsure whether a feature exists in that version, or when migrating/upgrading.

**Rule 2:** Always load the matching subskill before writing or reviewing Avalonia code. Subskills mark version-specific content as **(12.0+)**, **(12.1+)** or **(11.x)**; unlabelled content works in all supported versions.

## Routing Table

| Task | Load subskill |
|---|---|
| Which version? Feature availability, 11→12 migration, 12.0→12.1 upgrade | `avalonia-versions` |
| XAML markup, .axaml files, namespaces, code-behind | `avalonia-xaml` |
| Panels, Grid, StackPanel, alignment, margins | `avalonia-layout` |
| Styles, control themes, selectors, pseudoclasses, themes | `avalonia-styling` |
| `{Binding}`, compiled bindings, converters, validation | `avalonia-data-binding` |
| DataTemplate, ItemTemplate, template selection | `avalonia-data-templates` |
| MVVM pattern, INotifyPropertyChanged, ICommand, CommunityToolkit | `avalonia-mvvm` |
| Built-in controls (Button, TextBox, GroupBox, TableView, DataGrid, pages, etc.) | `avalonia-controls` |
| Custom UserControl, TemplatedControl, drawing controls | `avalonia-custom-controls` |
| Brushes, transforms, animations, transitions | `avalonia-graphics-animation` |
| Pointer, keyboard, gestures, drag-and-drop | `avalonia-input-interaction` |
| StyledProperty, DirectProperty, AttachedProperty | `avalonia-property-system` |
| Routed events, bubbling, tunneling | `avalonia-events` |
| Clipboard, file dialogs, notifications, storage | `avalonia-services` |
| App structure, resource dictionaries, app lifetime | `avalonia-app-development` |
| Headless testing, UI automation | `avalonia-testing` |
| Packaging for macOS/Windows/Linux/WASM | `avalonia-deployment` |
| Migrating from WPF | `avalonia-wpf-migration` |
| UI thread, `Dispatcher`, async/await, background work, timers | `avalonia-threading` |
| Android/iOS app concerns: lifetimes, safe areas, soft keyboard, back button, haptics | `avalonia-mobile` |
| Composition API, direct Skia/OpenGL, `NativeControlHost`, embedding Avalonia in WinForms/WPF/WinUI | `avalonia-interop-rendering` |
| Slow UI, profiling, DevTools, virtualization, startup time, trimming, NativeAOT | `avalonia-performance` |
| **Designing/polishing UI for visual quality, picking themes, building a design system, motion, accessibility, layout patterns, icons, pre-ship review** | `avalonia-pro-max` |

## Dependency policy

This repository does not add Avalonia community packages without an explicit card. Prefer
built-in controls and the existing token/style system; no package in the upstream community
list is approved by this skill alone.
