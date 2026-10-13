# Annotation editor line endings and native preset focus

Recovered Application run `38307917016` validated source tree
`001b60000972df3f462c520d3d6f4d8572d9ba10`: 749 core tests passed;
308 of 311 browser checks passed. Both Uno targets, all packages and isolated
consumers passed, as did native GL/GLES. Three browser failures remained.

## Text editing, not rendering

The preserved text failure has `text = "First line\rSecond line\rThird line"`
and `lines = 3`. Native multiline input was delivered and painted, but the
comparison expected LF separators. More importantly, applying unchanged initial
LF text from a CR-normalizing native editor could create an unnecessary history
entry; Undo also left stale text in the inspector.

`AnnotationTextContent` provides CR/LF/CRLF equivalence without normalized copies
and one-allocation canonical LF conversion. It preserves blank/trailing lines,
Unicode text and non-CR/LF characters. Rendering and editor submission share the
separator rules. Model `Text` assignment retains the exact supplied string for
non-editor consumers. Editors compare equivalent text before recording; genuinely
changed input is normalized at submission. Undo restores the exact prior model
value and updates the native inspector, while unrelated style changes and scene
redraws leave a user's pending draft untouched.

The existing strict LF model assertion remains. Browser coverage additionally
applies unchanged native text three times and checks no history, and checks that
Apply/Undo/Redo synchronize the native inspector. Core tests cover mixed separator
combinations, nulls, reference reuse, original model preservation and a seeded
scalar oracle. The warmed 10,000-comparison fixture allocates zero managed bytes;
it excludes normalization of changed text, UI, history, layout and rendering.

## Overview preset input

The saved overview screenshot still has the popup open with Million samples
selected. Opening a native popup and focus entering its item are distinct events.
The old test sent Home/ArrowDown/Enter after observing only `IsDropDownOpen`, so
keys could precede item focus. The next grip test inherited the interrupted popup.

The page now observes the actual focused ComboBoxItem index, without setting focus,
selection or application state. The browser test waits for a focused item, then
for the result of Home and each ArrowDown before Enter. It still requires the
requested preset and closed popup. Individual failure states are retained instead
of one overwritten JSON file. No timeouts, tolerances, input assertions or pixel
thresholds are relaxed. Final-head full browser validation is required.

References: Microsoft documents native multiline TextBox input at
https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/text-box and
the target text-annotation behavior is described at
https://www.scichart.com/documentation/win/current/The%20TextAnnotation%20Type.html.
The preserved input/geometry artifacts establish this specific failure, not
reference API/pixel equivalence or a complete review of all chart subsystems.
