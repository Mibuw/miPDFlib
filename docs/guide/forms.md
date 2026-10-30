# Forms (AcroForms)

## Reading and filling

```csharp
using var doc = PdfDocument.Open("antrag.pdf");
var form = doc.GetForm() ?? throw new InvalidOperationException("no form");

foreach (var field in form.Fields)
    Console.WriteLine($"{field.FullName} ({field.GetType().Name}) = {field.Value}");

form.SetValue("person.name", "Jane Doe");     // text field (fully qualified name)
form.SetValue("zustimmung", "Yes");                    // check box: on-state name or "Off"
form.GetField<PdfCheckBox>("newsletter")!.IsChecked = true;
form.SetValue("anrede", "Frau");                       // radio group: export value
form.SetValue("country", "Austria");                   // combo/list box: export value or display text
form.GetField<PdfChoiceField>("sprachen")!.SetSelection(new[] { "de", "en" });

doc.Save("antrag-ausgefuellt.pdf");
```

Setting a value regenerates the field's appearance, so the result looks correct in every viewer
(no reliance on `NeedAppearances`). Invalid options throw `ArgumentException`.

### Fonts for appearances

The font of the field's `/DA` is used if it is one of the standard fonts (Helv, TiRo, Cour, ...),
otherwise Helvetica. For characters outside WinAnsi set an embedded font:

```csharp
form.AppearanceFont = PdfTrueTypeFont.FromFile("arial.ttf");          // for all fields
form.GetField("name")!.AppearanceFont = PdfTrueTypeFont.FromFile("...");  // per field
```

Supported appearance features: single/multi-line text with word wrap, auto font size (`0 Tf`),
alignment (`/Q`), comb fields, password fields, max length, background and border colors (`/MK`),
widget rotation (`/MK /R`), check boxes and radio buttons via their `/AP` states, combo and list boxes.

## Creating fields

```csharp
var page = doc.GetPage(1);
var form = doc.GetOrCreateForm();
form.AddTextField(page, "name", new PdfRectangle(150, 700, 400, 720), "default value");
form.AddTextField(page, "remark", new PdfRectangle(150, 600, 400, 690), multiline: true);
form.AddCheckBox(page, "agree", new PdfRectangle(150, 560, 164, 574), isChecked: true);
form.AddRadioGroup(page, "size", new[] { ("S", new PdfRectangle(150, 520, 164, 534)),
                                          ("M", new PdfRectangle(180, 520, 194, 534)) }, selected: "M");
form.AddComboBox(page, "country", new PdfRectangle(150, 480, 300, 498), new[] { "AT", "DE", "CH" }, "AT");
form.AddListBox(page, "langs", new PdfRectangle(150, 400, 300, 470), new[] { "de", "en", "fr" });
form.AddSignatureField(page, "signature", new PdfRectangle(350, 80, 550, 140));

var zip = form.AddTextField(page, "zip", new PdfRectangle(150, 360, 250, 378));
zip.Flags |= FieldFlags.Comb; zip.MaxLength = 5; zip.Value = "1010";
```

### Push buttons with images, check box styles

```csharp
var button = form.AddPushButton(page, "signatureImage", new PdfRectangle(50, 600, 250, 660), PdfImage.FromBytes(png));
button.ReadOnly = true;
button.SetImage(PdfImage.FromBytes(otherPng));            // replace the icon later (icon only)
form.AddPushButton(page, "ok", rect, caption: "OK", border: PdfColor.Black);
form.AddCheckBox(page, "agree", rect, isChecked: true, style: CheckBoxStyle.Cross);   // Check, Cross, Circle, Square, Diamond
```

### Borders, colors and rotation

`AddTextField` and `AddCheckBox` draw a black 1 pt border by default; `borderWidth: 0` creates the field without
border (no `/MK /BC`, so viewers that rebuild the appearance do not add one either). Border, background and rotation
of existing widgets can be changed later; the appearance of text, choice, check box and radio button fields is then
rebuilt (check boxes and radio buttons keep their state):

```csharp
form.AddTextField(page, "plain", rect, "value", borderWidth: 0);
var box = form.AddCheckBox(page, "agree", rect, borderWidth: 0);
box.Widgets[0].BackgroundColor = null;                    // transparent instead of white
box.Widgets[0].Rotation = 90;                             // /MK /R, counterclockwise: 0, 90, 180, 270
box.Widgets[0].BorderColor = PdfColor.Rgb(0, 0, 0.6);     // null = no border
box.Widgets[0].BorderWidth = 2;                           // /BS /W
box.RegenerateAppearance();                               // after editing /MK or /BS directly
```

## Flattening

```csharp
form.Flatten();                                     // all fields
form.Flatten(new[] { form.GetField("name")! });     // selected fields
```

The current appearance is drawn into the page content and the fields are removed; the document
no longer contains an interactive form if all fields were flattened.

## Other operations

| Operation | API |
|---|---|
| Remove a field | `form.RemoveField(field)` |
| Read-only / required | `field.ReadOnly = true; field.Required = true;` |
| Tooltip | `field.Tooltip = "..."` |
| All values | `form.GetValues()` |
| Widgets (position, page) | `field.Widgets[i].Rect`, `.Page` |

When documents are merged (`ImportPages`, `Merge`), the fields of the imported pages are added to the
target form together with the default resources. Fields with identical names are not renamed.

Not supported: XFA forms (only their AcroForm part), JavaScript actions/calculations, FDF/XFDF import.

## XFA data

For forms that contain XFA (usually "static" XFA forms with AcroForm fields), the XFA form data can be read and
replaced. Rendering of dynamic XFA forms is not supported.

```csharp
var xfa = doc.GetForm()!.Xfa;
if (xfa.Exists)
{
    string? data = xfa.GetData();                                   // content of xfa:data
    xfa.FillData("<form1><Name>Jane Doe</Name></form1>");     // data root, xfa:data or xfa:datasets
    // xfa.Remove();   // drop XFA so that viewers use the AcroForm fields
}
```
