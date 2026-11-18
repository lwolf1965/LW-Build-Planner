// From CometWorks/client2-plugin-template (MIT, (c) 2026 Viktor Ferenczi), used unchanged unless noted.
using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace ClientPlugin.Settings.Elements;

internal interface IElement
{
    Control BuildRow(string name, Func<object> getter, Action<object> setter);
    List<Type> SupportedTypes { get; }
}
