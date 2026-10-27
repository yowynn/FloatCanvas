using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class FontEditingStateTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        _=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        string output=Environment.GetEnvironmentVariable("FLOATCANVAS_FONT_EDITING_OUTPUT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex","artifacts","FloatCanvas-global-hidden-refresh","verification","font-editing");
        Directory.CreateDirectory(output);
        var oldCulture=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("zh-CN");
        var findings=new List<(string Name,bool Passed)>();var observations=new List<object>();
        try
        {
            foreach(bool overlay in new[]{false,true})
            {
                string label=overlay?"overlay":"ordinary";
                var root=new StackPanel{Opacity=0};
                using var source=new HwndSource(new HwndSourceParameters("FloatCanvas.FontEditing."+label)
                {Width=440,Height=160,PositionX=-12000,PositionY=-12000,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x080000A0}){RootVisual=root};
                using var scope=overlay?new OverlayWindowScope(source.Handle,()=>true):null;
                AppTheme.Apply("light");
                var picker=(FontPickerControl)ToolOptionPreviews.FontPicker();var sibling=new TextBox{Text="Focus destination"};
                root.Children.Add(picker);root.Children.Add(sibling);ShowWindow(source.Handle,4);Pump();picker.ApplyTemplate();
                var editor=(TextBox)picker.Template.FindName("PART_EditableTextBox",picker);
                var popupChild=picker.Template.FindName("PART_Popup",picker) switch{Popup popup=>popup.Child,OverlayPopup popup=>popup.Child!,_=>throw new InvalidOperationException("Missing production font popup")};
                popupChild.Opacity=0;
                var catalog=picker.Items.OfType<FontPickerControl.FontChoice>().ToArray();int allFonts=catalog.Length;
                string model="Arial";int commits=0;
                picker.FontCommitted+=name=>{model=name;commits++;};
                try
                {
                    Reset();
                    string typed="";
                    foreach(char letter in "Segoe")
                    {
                        TypeLetter(letter);typed+=letter;Wait();
                        Observe("typing "+typed+" retains the exact editor text",editor.Text==typed);
                        Observe("typing "+typed+" retains its trailing caret",editor.SelectionStart==typed.Length&&editor.SelectionLength==0);
                        Observe("typing "+typed+" remains in the actual text editor",editor.IsKeyboardFocusWithin);
                    }
                    Observe("text search never publishes a font model change",model=="Arial"&&commits==0);
                    Key(System.Windows.Input.Key.Down);Key(System.Windows.Input.Key.Down);Key(System.Windows.Input.Key.Up);
                    Observe("candidate navigation never publishes a font model change",model=="Arial"&&commits==0);
                    Observe("candidate navigation preserves the typed search draft",editor.Text==typed);
                    Snapshot("typed-and-navigated");
                    Key(System.Windows.Input.Key.PageDown);Key(System.Windows.Input.Key.PageUp);
                    Observe("page navigation never publishes a font model change",model=="Arial"&&commits==0);
                    Observe("page navigation preserves the typed search draft",editor.Text==typed);
                    Snapshot("page-navigated");

                    Reset();TypeText("o");Wait();
                    var scroll=Descendants(popupChild).OfType<ScrollViewer>().First();
                    var bar=Descendants(scroll).OfType<ScrollBar>().Single(value=>value.Orientation==Orientation.Vertical);
                    var track=(Track)bar.Template.FindName("PART_Track",bar);
                    Observe("an actual search exposes a scrollable font-candidate thumb",picker.Items.Count>0&&picker.Items.Count<allFonts&&bar.IsVisible&&track.Thumb.IsVisible);
                    scroll.ScrollToHome();Pump();double beforeScroll=scroll.VerticalOffset;
                    MouseClick(track.Thumb,()=>{track.Thumb.RaiseEvent(new DragDeltaEventArgs(0,35));Pump();});
                    Observe("routed scrollbar-thumb interaction scrolls the search candidates",scroll.VerticalOffset>beforeScroll);
                    Observe("scrolling search candidates keeps the popup open without committing",picker.IsDropDownOpen&&!picker.IsReadOnly&&model=="Arial"&&commits==0);
                    Observe("scrolling search candidates preserves the typed search draft",editor.Text=="o");
                    Snapshot("search-scrollbar-interaction");

                    Reset();TypeText("SegUI");Wait();editor.Select(3,0);TypeText("oe ");Wait();
                    Observe("typing in the middle preserves the entire draft",editor.Text=="Segoe UI");
                    Observe("filter refresh preserves a middle caret",editor.SelectionStart==6&&editor.SelectionLength==0);
                    editor.Select(6,2);TypeText("Script");Wait();
                    Observe("typing over a selected range replaces only that range",editor.Text=="Segoe Script");
                    Observe("range replacement retains its resulting caret",editor.SelectionStart==12&&editor.SelectionLength==0);
                    Observe("middle edits and replacements do not commit a font",model=="Arial"&&commits==0);
                    Snapshot("middle-caret-and-range-replacement");

                    Reset();TypeText("Segoe");Wait();
                    var candidates=picker.Items.OfType<FontPickerControl.FontChoice>().ToArray();
                    Observe("font search offers installed candidates",candidates.Length>0&&candidates.Length<allFonts);
                    if(candidates.Length>0)
                    {
                        var expected=candidates[0];Key(System.Windows.Input.Key.Enter);Pump();
                        Observe("Enter commits the first candidate's canonical font name",model==expected.Name&&ToolOptionPreviews.SelectedFontName(picker)==expected.Name);
                        Observe("Enter displays the committed candidate's friendly name",picker.Text==expected.DisplayName&&editor.Text==expected.DisplayName);
                        Observe("Enter publishes exactly one font commit",commits==1);
                        Observe("a committed font returns to a read-only closed display",picker.IsReadOnly&&editor.IsReadOnly&&!picker.IsDropDownOpen);
                        Wait();
                        Observe("a focused collapsed font displays its name without a selected text range",editor.IsKeyboardFocusWithin&&editor.SelectionLength==0);
                        MouseClick(editor);Keyboard.Focus(editor);Pump();
                        Observe("the first mouse activation selects the entire font name for editing",!picker.IsReadOnly&&picker.IsDropDownOpen&&editor.SelectionLength==editor.Text.Length&&editor.SelectionStart==0);
                        editor.Select(Math.Min(2,editor.Text.Length),0);MouseClick(editor);Pump();
                        Observe("another editor click does not select the entire name again",editor.SelectionLength==0);
                        Key(System.Windows.Input.Key.Enter);Pump();
                    }
                    Snapshot("enter-commit");

                    Reset();TypeText("Segoe");Wait();candidates=picker.Items.OfType<FontPickerControl.FontChoice>().ToArray();
                    if(candidates.Length>0)
                    {
                        var expected=candidates[0];Keyboard.Focus(sibling);Pump();
                        Observe("editor focus loss commits its default candidate once",model==expected.Name&&picker.Text==expected.DisplayName&&commits==1);
                    }
                    Snapshot("focus-loss-commit");

                    Reset();TypeText("Segoe");
                    var latest=catalog.First(font=>font.Name.Contains("Segoe",StringComparison.CurrentCultureIgnoreCase)||font.DisplayName.Contains("Segoe",StringComparison.CurrentCultureIgnoreCase));
                    var pending=(DispatcherTimer)typeof(FontPickerControl).GetField("search",Private)!.GetValue(picker)!;
                    Observe("immediate focus loss starts before the search debounce runs",pending.IsEnabled);
                    Keyboard.Focus(sibling);Pump();
                    Observe("immediate focus loss commits the latest draft's first candidate once",model==latest.Name&&picker.Text==latest.DisplayName&&commits==1);
                    Snapshot("immediate-focus-loss-commit");

                    Reset();TypeText("Segoe");Wait();
                    bool sawNullFocus=false;KeyboardFocusChangedEventHandler lostToNull=(_,e)=>{if(e.NewFocus is null)sawNullFocus=true;};
                    picker.LostKeyboardFocus+=lostToNull;
                    try{Keyboard.ClearFocus();Pump();}
                    finally{picker.LostKeyboardFocus-=lostToNull;}
                    Observe("the null-focus case passes through a real WPF focus-loss event",sawNullFocus);
                    Observe("a visible search commits its first candidate once when focus becomes null",model==latest.Name&&commits==1&&picker.Text==latest.DisplayName&&picker.IsReadOnly&&!picker.IsDropDownOpen);
                    Snapshot("null-focus-commit");

                    Reset();TypeText("Segoe");Wait();editor.SelectAll();Key(System.Windows.Input.Key.Back);
                    Observe("deleting a nonempty search produces an actual empty editor",editor.Text==""&&model=="Arial"&&commits==0);
                    Key(System.Windows.Input.Key.Enter);Pump();
                    Observe("Enter on a cleared search leaves an empty read-only display without a model commit",editor.Text==""&&picker.Text==""&&picker.IsReadOnly&&model=="Arial"&&commits==0);
                    Observe("Enter on a cleared search does not change the font catalog",picker.Items.OfType<FontPickerControl.FontChoice>().Select(font=>font.Name).SequenceEqual(catalog.Select(font=>font.Name)));
                    Snapshot("cleared-search-enter");

                    Reset();TypeText("Segoe");Wait();editor.SelectAll();Key(System.Windows.Input.Key.Back);
                    Observe("a cleared-search blur starts before its debounce runs",pending.IsEnabled);
                    Keyboard.Focus(sibling);Pump();
                    Observe("immediate blur on a cleared search leaves an empty read-only display without a model commit",editor.Text==""&&picker.Text==""&&picker.IsReadOnly&&model=="Arial"&&commits==0);
                    Observe("blur on a cleared search does not change the font catalog",picker.Items.OfType<FontPickerControl.FontChoice>().Select(font=>font.Name).SequenceEqual(catalog.Select(font=>font.Name)));
                    Snapshot("cleared-search-blur");

                    Reset();TypeText("Segoe");Wait();candidates=picker.Items.OfType<FontPickerControl.FontChoice>().ToArray();
                    if(candidates.Length>0)
                    {
                        var expected=candidates[Math.Min(1,candidates.Length-1)];
                        picker.UpdateLayout();popupChild.UpdateLayout();
                        var item=picker.ItemContainerGenerator.ContainerFromItem(expected) as ComboBoxItem;
                        Observe("a real font candidate container is realized",item is not null);
                        if(item is not null)
                        {
                            int presses=0,releases=0;
                            MouseButtonEventHandler down=(_,_)=>presses++,up=(_,_)=>releases++;
                            item.AddHandler(Mouse.PreviewMouseDownEvent,down,true);item.AddHandler(Mouse.PreviewMouseUpEvent,up,true);
                            try{MouseClick(item);}
                            finally
                            {
                                item.RemoveHandler(Mouse.PreviewMouseDownEvent,down);item.RemoveHandler(Mouse.PreviewMouseUpEvent,up);
                            }
                            Observe("a candidate click reaches routed WPF mouse component down and up events",presses==1&&releases==1);
                            Observe("a candidate click commits its canonical name exactly once",model==expected.Name&&commits==1);
                            Observe("a candidate click ends editing with its friendly display name",picker.Text==expected.DisplayName&&editor.Text==expected.DisplayName&&!picker.IsDropDownOpen);
                        }
                    }
                    Snapshot("mouse-candidate-commit");

                    Reset();TypeText("Segoe");Wait();candidates=picker.Items.OfType<FontPickerControl.FontChoice>().ToArray();
                    if(candidates.Length>0)
                    {
                        var expected=candidates[0];var arrow=(ToggleButton)picker.Template.FindName("DropDownToggle",picker);
                        MouseClick(arrow);
                        Observe("arrow commits the candidate once before opening full selection",model==expected.Name&&picker.Text==expected.DisplayName&&commits==1);
                        Observe("arrow opens the complete read-only font list",picker.IsDropDownOpen&&picker.Items.Count==allFonts&&(!picker.IsEditable||picker.IsReadOnly||editor.IsReadOnly));
                        Observe("full browsing displays the font name without editable text selection",editor.SelectionLength==0);
                        int committedBefore=commits;MouseClick(editor);Keyboard.Focus(editor);Pump();editor.SelectAll();TypeLetter('M');Wait();
                        Observe("clicking the editor leaves full browsing and starts a fresh search",editor.Text=="M"&&!editor.IsReadOnly&&picker.Items.Count>0&&picker.Items.Count<allFonts);
                        Observe("returning to editing does not commit its new search draft",commits==committedBefore&&model==expected.Name);
                    }
                    Snapshot("arrow-full-selection");

                    Reset();const string absent="__font_that_is_not_installed_8462__";TypeText(absent);Wait();
                    Observe("an unmatched edit has an empty candidate list",picker.Items.Count==0);
                    Key(System.Windows.Input.Key.Enter);Pump();
                    Observe("an unmatched edit cannot publish an arbitrary family string",model=="Arial"&&commits==0&&picker.Items.OfType<FontPickerControl.FontChoice>().All(font=>font.Name!=absent));
                    Snapshot("unmatched-edit");

                    var localized=catalog.FirstOrDefault(font=>font.Installed&&font.Name.Equals("STKaiti",StringComparison.OrdinalIgnoreCase)&&font.DisplayName!=font.Name) ??
                        catalog.FirstOrDefault(font=>font.Installed&&font.DisplayName!=font.Name&&font.DisplayName.Any(letter=>letter>127));
                    Observe("the fixture has a real localized font family",localized is not null);
                    if(localized is not null)
                    {
                        Reset();Keyboard.Focus(sibling);picker.SelectFont(localized.Name);Pump();
                        Observe("model synchronization displays its friendly Chinese name",picker.Text==localized.DisplayName&&editor.Text==localized.DisplayName);
                        Observe("model synchronization retains its canonical family name",ToolOptionPreviews.SelectedFontName(picker)==localized.Name);
                        Reset();TypeText(localized.DisplayName);Wait();
                        var expected=picker.Items.OfType<FontPickerControl.FontChoice>().FirstOrDefault();
                        Observe("a Chinese friendly font name finds a real installed candidate",expected is not null);
                        if(expected is not null)
                        {
                            Key(System.Windows.Input.Key.Enter);Pump();
                            Observe("a Chinese-name search commits the canonical name once",model==expected.Name&&commits==1);
                            Observe("a Chinese-name search displays the friendly name after commit",picker.Text==expected.DisplayName&&editor.Text==expected.DisplayName);
                        }
                        Snapshot("localized-search-commit");
                    }
                    root.Visibility=Visibility.Collapsed;editor.Text="hidden font edit";Wait();
                    Observe("hidden editors cannot leave a detached font dropdown",!picker.IsDropDownOpen&&!popupChild.IsVisible);
                    if(!overlay)CheckToolOptionsOwner();
                }
                finally{picker.IsDropDownOpen=false;Keyboard.ClearFocus();scope?.ClosePopups();source.RootVisual=null;}

                void Observe(string name,bool passed){findings.Add((label+" "+name,passed));Console.WriteLine($"FONT_EDITING_FINDING {passed}: {label} {name}");}
                void Snapshot(string phase)
                {
                    observations.Add(new{Backend=label,Phase=phase,Text=editor.Text,editor.SelectionStart,editor.SelectionLength,Model=model,Commits=commits,Items=picker.Items.Count,picker.IsDropDownOpen,picker.IsEditable,picker.IsReadOnly,EditorReadOnly=editor.IsReadOnly,
                        Selected=(picker.SelectedItem as FontPickerControl.FontChoice)?.Name,Candidate=(typeof(FontPickerControl).GetField("candidate",Private)!.GetValue(picker) as FontPickerControl.FontChoice)?.Name});
                    Console.WriteLine("FONT_EDITING "+JsonSerializer.Serialize(observations[^1]));
                }
                void Reset()
                {
                    picker.IsDropDownOpen=false;Keyboard.Focus(sibling);Pump();picker.SelectFont("Arial");model="Arial";commits=0;
                    SetFocus(source.Handle);MouseClick(editor);Keyboard.Focus(editor);Pump();editor.SelectAll();
                    Observe("a new edit starts with a loaded visible focused TextBox",picker.IsLoaded&&picker.IsVisible&&editor.IsKeyboardFocusWithin);
                    Observe("a real editor entry immediately opens an editable search popup",picker.IsDropDownOpen&&!picker.IsReadOnly&&!editor.IsReadOnly);
                }
                void TypeLetter(char letter)
                {
                    nint focus=GetFocus();if(focus==0)throw new InvalidOperationException("Font character has no native focus target");
                    SendMessage(focus,0x102,letter,1);Pump();
                }
                void TypeText(string text){foreach(char letter in text)TypeLetter(letter);}
                void Key(System.Windows.Input.Key key)
                {
                    using var modifiers = new ThreadModifierScope(ModifierKeys.None);
                    uint virtualKey=(uint)KeyInterop.VirtualKeyFromKey(key);uint scan=MapVirtualKey(virtualKey,0);
                    var original=new byte[256];GetKeyboardState(original);
                    try{foreach(bool up in new[]{false,true}){var state=(byte[])original.Clone();state[virtualKey]=up?(byte)0:(byte)128;SetKeyboardState(state);nint focus=GetFocus();if(focus==0)break;uint data=1|((scan&255)<<16)|(up?0xC0000000u:0);if(Keyboard.Modifiers!=ModifierKeys.None)throw new InvalidOperationException("Font key fixture requires a plain key with no modifiers.");SendMessage(focus,up?0x101u:0x100u,(nint)virtualKey,unchecked((nint)(int)data));Pump();}}
                    finally{SetKeyboardState(original);}
                }
                void CheckToolOptionsOwner()
                {
                    var expected=catalog.First(font=>font.Name.Contains("Segoe",StringComparison.CurrentCultureIgnoreCase)||font.DisplayName.Contains("Segoe",StringComparison.CurrentCultureIgnoreCase));
                    root.Children.Clear();root.Visibility=Visibility.Visible;
                    var controller=new EditorController(new EditorSession()){Tool=EditorTool.Text};controller.Style.FontFamily="Arial";
                    var owner=new ToolOptionsPanel(controller,new ToolPresetLibrary(Path.Combine(output,"owner-presets.json")),new ColorLibrary(Path.Combine(output,"owner-colors.json")));
                    root.Children.Add(owner);root.Children.Add(sibling);owner.PrepareForShow();Pump();
                    var ownedPicker=(FontPickerControl)typeof(ToolOptionsPanel).GetField("fontFamily",Private)!.GetValue(owner)!;ownedPicker.ApplyTemplate();root.UpdateLayout();Pump();
                    var ownedEditor=(TextBox)ownedPicker.Template.FindName("PART_EditableTextBox",ownedPicker);
                    var ownedPopup=ownedPicker.Template.FindName("PART_Popup",ownedPicker) switch{Popup popup=>popup.Child,OverlayPopup popup=>popup.Child!,_=>throw new InvalidOperationException("Missing owner font popup")};ownedPopup.Opacity=0;
                    int changes=0;owner.ParametersChanged+=()=>{changes++;owner.Refresh();};
                    SetFocus(source.Handle);MouseClick(ownedEditor);Keyboard.Focus(ownedEditor);Pump();ownedEditor.SelectAll();
                    Observe("the real tool-options owner starts from a loaded editable font search",ownedPicker.IsLoaded&&ownedEditor.IsLoaded&&ownedEditor.IsKeyboardFocusWithin&&!ownedPicker.IsReadOnly&&ownedPicker.IsDropDownOpen);
                    OwnerSnapshot("owner-edit-start");
                    TypeText("Segoe");
                    Observe("the real tool-options owner receives no intermediate font changes",controller.Style.FontFamily=="Arial"&&changes==0);
                    OwnerSnapshot("owner-typed");
                    var destination=Keyboard.Focus(sibling);Pump();OwnerSnapshot("owner-blur",destination?.GetType().Name);
                    Observe("the real tool-options owner commits the latest font on blur once",controller.Style.FontFamily==expected.Name&&changes==1);
                    Observe("the real tool-options owner refresh keeps the friendly font display",ownedPicker.Text==expected.DisplayName&&ownedEditor.Text==expected.DisplayName);
                    ownedPicker.IsDropDownOpen=false;

                    controller.Tool=EditorTool.Text;controller.Style.FontFamily="Arial";owner.Refresh();Pump();changes=0;
                    SetFocus(source.Handle);MouseClick(ownedEditor);Keyboard.Focus(ownedEditor);Pump();ownedEditor.SelectAll();TypeText("Segoe");Wait();
                    Observe("a tool-context change starts with an uncommitted font search",controller.Style.FontFamily=="Arial"&&changes==0&&ownedEditor.Text=="Segoe"&&ownedPicker.Items.Count>0&&ownedPicker.Items.Count<allFonts);
                    controller.Tool=EditorTool.Number;owner.Refresh();Pump();
                    Observe("switching tools with the same current font discards the prior search",controller.Tool==EditorTool.Number&&controller.Style.FontFamily=="Arial"&&changes==0&&ownedEditor.Text=="Arial"&&!ownedPicker.IsDropDownOpen);
                    Keyboard.Focus(sibling);Pump();
                    Observe("blur after a same-font tool switch cannot apply the old candidate to the new tool",controller.Tool==EditorTool.Number&&controller.Style.FontFamily=="Arial"&&changes==0&&ownedPicker.Text=="Arial");
                    void OwnerSnapshot(string phase,string? destination=null)
                    {
                        var observed=new{Backend=label,Phase=phase,Model=controller.Style.FontFamily,Changes=changes,Text=ownedEditor.Text,Selected=(ownedPicker.SelectedItem as FontPickerControl.FontChoice)?.Name,
                            Mode=typeof(FontPickerControl).GetField("mode",Private)!.GetValue(ownedPicker)?.ToString(),Candidate=(typeof(FontPickerControl).GetField("candidate",Private)!.GetValue(ownedPicker) as FontPickerControl.FontChoice)?.Name,
                            ownedPicker.IsDropDownOpen,ownedPicker.IsReadOnly,EditorFocus=ownedEditor.IsKeyboardFocusWithin,Focus=Keyboard.FocusedElement?.GetType().Name,Destination=destination,Valid=controller.Parameters.IsValid(),
                            ownedPicker.IsLoaded,ownedPicker.IsVisible,ownedPicker.ActualWidth,ownedPicker.ActualHeight,Updating=typeof(FontPickerControl).GetField("updating",Private)!.GetValue(ownedPicker),EditorSame=ReferenceEquals(ownedEditor,typeof(FontPickerControl).GetField("editor",Private)!.GetValue(ownedPicker)),
                            Status=((TextBlock)typeof(ToolOptionsPanel).GetField("status",Private)!.GetValue(owner)!).Text};
                        observations.Add(observed);Console.WriteLine("FONT_EDITING "+JsonSerializer.Serialize(observed));
                    }
                }
            }
        }
        finally{CultureInfo.CurrentUICulture=oldCulture;File.WriteAllText(Path.Combine(output,"font-editing-state.json"),JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));}
        foreach(var finding in findings)check(finding.Name,finding.Passed);
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void Wait(){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(350)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}}
    private static void MouseClick(UIElement target,Action? whilePressed=null)
    {
        // Component-level routed mouse semantics, not native pointer acceptance. A second
        // MouseDevice must not participate in InputManager promotion: both it and Primary
        // would promote each preview using different targets and cause an outside click.
        var device=(MouseDevice)Activator.CreateInstance(MouseType.Value,InputManager.Current,target)!;
        ToolOptionsInputFixture.ReleaseControlledMouse(device);
        try
        {
            var down=new MouseButtonEventArgs(device,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent,Source=target};
            target.RaiseEvent(down);down.RoutedEvent=Mouse.MouseDownEvent;target.RaiseEvent(down);Pump();
            whilePressed?.Invoke();
            MouseType.Value.GetField("buttonState")!.SetValue(device,MouseButtonState.Released);
            var up=new MouseButtonEventArgs(device,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseUpEvent,Source=target};
            target.RaiseEvent(up);up.RoutedEvent=Mouse.MouseUpEvent;target.RaiseEvent(up);Pump();
        }
        finally{device.Capture(null);}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for(int index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
            foreach(var child in Descendants(VisualTreeHelper.GetChild(root,index)))yield return child;
    }
    private static readonly Lazy<Type> MouseType=new(()=>
    {
        var assembly=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FloatCanvasFontEditingControlledInput"),AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)])!,["PresentationCore"]));
        var type=assembly.DefineDynamicModule("ControlledInput").DefineType("FontEditingMouse",TypeAttributes.Public,typeof(MouseDevice));
        var target=type.DefineField("target",typeof(IInputElement),FieldAttributes.Public);var buttonState=type.DefineField("buttonState",typeof(MouseButtonState),FieldAttributes.Public);
        var constructor=type.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,[typeof(InputManager),typeof(IInputElement)]);var il=constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(MouseDevice).GetConstructors(Private).Single());
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Stfld,target);
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Stfld,buttonState);il.Emit(OpCodes.Ret);
        var method=typeof(MouseDevice).GetMethod("GetButtonStateFromSystem",Private)!;
        var getter=type.DefineMethod(method.Name,MethodAttributes.Assembly|MethodAttributes.Virtual,typeof(MouseButtonState),[typeof(MouseButton)]);il=getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,buttonState);il.Emit(OpCodes.Ret);type.DefineMethodOverride(getter,method);
        method=typeof(MouseDevice).GetProperty("Target")!.GetMethod!;
        getter=type.DefineMethod(method.Name,MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.SpecialName,typeof(IInputElement),Type.EmptyTypes);il=getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,target);il.Emit(OpCodes.Ret);type.DefineMethodOverride(getter,method);
        return type.CreateType()!;
    });
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")]private static extern nint GetFocus();
    [DllImport("user32.dll",EntryPoint="SendMessageW",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern nint SendMessage(nint window,uint message,nint key,nint flags);
    [DllImport("user32.dll")]private static extern uint MapVirtualKey(uint key,uint type);
    [DllImport("user32.dll")]private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")]private static extern bool SetKeyboardState(byte[] state);
}
