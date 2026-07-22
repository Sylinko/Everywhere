global using System;
global using System.Collections;
global using System.Collections.Generic;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Reactive.Disposables;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using Avalonia;
global using DynamicData;
global using DynamicData.Binding;
global using Everywhere.Extensions;
global using Everywhere.I18N;
global using LocaleKey = Everywhere.Core.I18N.LocaleKey;
global using LocaleResolver = Everywhere.Core.I18N.LocaleResolver;
global using Everywhere.ViewModels;
global using ShadUI.Extensions;
global using ZLinq;
using Avalonia.Metadata;

[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Everywhere.MarkupExtensions")]
[assembly: XmlnsDefinition("https://github.com/avaloniaui", "Everywhere.Core.I18N")]