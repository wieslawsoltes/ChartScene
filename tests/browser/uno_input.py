"""Real input acknowledged by managed routing, with observed layout-change recovery."""
from __future__ import annotations
from pathlib import Path
import json
import time

class UnoInput:
    def __init__(self, page, output: Path):
        self.page = page
        self.output = output
        self.actions = []

    @staticmethod
    def _center(bounds):
        return bounds['x']+bounds['width']/2, bounds['y']+bounds['height']/2

    def click(self, target: str, bounds: dict, *, resolve=None) -> None:
        """Acknowledge the native target and settled bounds before exactly one down/up pair."""
        page = self.page
        action = {'target': target, 'positions': [], 'approaches': [], 'presses': 0}
        self.actions.append(action)
        def events(): return page.evaluate('window.chartSceneInputEvents || []')
        def sequence(): return page.evaluate('window.chartSceneInput?.sequence || 0')
        def matches(event, kind, after, x, y):
            return (event.get('sequence', 0)>after and event.get('target')==target
                and event.get('kind')==kind and abs(event['x']-x)<1 and abs(event['y']-y)<1)
        def current():
            value = resolve() if resolve is not None else bounds
            if not value or value['width']<=0 or value['height']<=0:
                raise AssertionError('The input target has no current layout: '+target)
            return value
        deadline = time.monotonic()+20
        try:
            while True:
                geometry = current(); x,y = self._center(geometry); before = sequence()
                action['positions'].append({'x':x,'y':y,'bounds':dict(geometry)})
                previous = events()
                same_position = bool(previous and abs(previous[-1]['x']-x)<1 and abs(previous[-1]['y']-y)<1)
                already_here = same_position and previous[-1].get('target')==target
                if not already_here:
                    # Another native row can move underneath a stationary pointer.
                    # A same-coordinate mouse.move need not dispatch an event. Make
                    # one real, in-bounds approach and return to the intended center;
                    # the exact control must still acknowledge movement before Down.
                    if same_position:
                        approach = (x+min(2.0, geometry['width']/4), y)
                        action['approaches'].append({'x':approach[0], 'y':approach[1]})
                        page.mouse.move(*approach)
                    page.mouse.move(x,y)
                moved = already_here
                while not moved and time.monotonic()<deadline:
                    moved = any(matches(e,'move',before,x,y) for e in events())
                    if current()!=geometry: break
                    if not moved: page.wait_for_timeout(16)
                # Font/layout completion can relocate controls after model readiness.
                # Read newly published bounds and move again, but never repeat a click.
                if current()!=geometry:
                    if time.monotonic()>=deadline: raise TimeoutError('Input target layout did not settle: '+target)
                    continue
                if not moved: raise TimeoutError('Pointer did not reach the managed input target: '+target)
                break
            before = sequence(); action['presses'] += 1
            page.mouse.down()
            try:
                page.wait_for_function('''p => (window.chartSceneInputEvents || []).some(e =>
                    e.sequence > p.after && e.target === p.target && e.kind === 'down' &&
                    Math.abs(e.x-p.x)<1 && Math.abs(e.y-p.y)<1)''',
                    arg={'after':before,'target':target,'x':x,'y':y})
            finally: page.mouse.up()
            action['delivered'] = True
        finally:
            self.output.mkdir(parents=True, exist_ok=True)
            (self.output/'managed-input.json').write_text(json.dumps(events(), indent=2))
            (self.output/'input-actions.json').write_text(json.dumps(self.actions, indent=2))
