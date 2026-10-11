"""Real input with managed-dispatch acknowledgement, not fixed sleeps or repeated clicks."""
from __future__ import annotations
from pathlib import Path
import json

class UnoInput:
    def __init__(self, page, output: Path):
        self.page = page
        self.output = output

    def click(self, target: str, bounds: dict) -> None:
        x, y = bounds['x'] + bounds['width'] / 2, bounds['y'] + bounds['height'] / 2
        def sequence(): return self.page.evaluate('window.chartSceneInput?.sequence || 0')
        def acknowledged(kind, after):
            self.page.wait_for_function('''p => (window.chartSceneInputEvents || []).some(e =>
                e.sequence > p.after && e.target === p.target && e.kind === p.kind &&
                Math.abs(e.x-p.x)<1 && Math.abs(e.y-p.y)<1)''',
                arg={'after': after, 'target': target, 'kind': kind, 'x': x, 'y': y})
        try:
            before = sequence()
            self.page.mouse.move(x, y)
            acknowledged('move', before)
            before = sequence()
            self.page.mouse.down()
            try: acknowledged('down', before)
            finally: self.page.mouse.up()
            # A popup may own the release rather than the pressed ComboBox. The caller
            # asserts the resulting semantic state instead of requiring that event route.
        finally:
            self.output.mkdir(parents=True, exist_ok=True)
            (self.output/'managed-input.json').write_text(json.dumps(
                self.page.evaluate('window.chartSceneInputEvents || []'), indent=2))
