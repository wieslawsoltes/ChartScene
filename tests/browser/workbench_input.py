"""Read-only layout/dispatch synchronization for the actual Uno examples workbench."""
from __future__ import annotations
from pathlib import Path
from uno_input import UnoInput


class WorkbenchInput:
    COMMANDS = {
        'browser-back', 'browser-forward', 'browser-home', 'navigation-toggle', 'example-tab',
        'source-tab', 'export-png', 'export-csv', 'info-toggle', 'download-source', 'pan',
        'zoom-rectangle', 'home', 'select', 'annotate', 'undo', 'redo', 'zoom-in', 'zoom-out',
        'play', 'apply-ranges',
    }

    def __init__(self, page, output: Path):
        self.page = page
        self.driver = UnoInput(page, output)

    def state(self):
        return self.page.evaluate('window.chartScene || {}')

    def bounds(self, name: str):
        # Page SizeChanged precedes descendant arrange. Only click an actually measured,
        # reachable control, not an earlier width/height or a placeholder (-1,-1) transform.
        self.page.wait_for_function('''name => {
            const b=window.chartScene?.controls?.[name];
            return b && [b.x,b.y,b.width,b.height].every(Number.isFinite) &&
                b.width>0 && b.height>0 && b.x>=0 && b.y>=0 &&
                b.x+b.width<=innerWidth+0.5 && b.y+b.height<=innerHeight+0.5;
        }''', arg=name)
        return self.state()['controls'][name]

    def click(self, name: str):
        before = self.page.evaluate('window.chartSceneBrowser?.actionSequence || 0')
        self.driver.click('browser-'+name, self.bounds(name),
                          resolve=lambda: self.state()['controls'].get(name))
        expected = 'example:'+name.split(':', 1)[1] if name.startswith('tile:') else name
        if name in self.COMMANDS or name.startswith(('example:', 'tile:')):
            self.page.wait_for_function('''p => window.chartSceneBrowser.actionSequence>p.before &&
                window.chartSceneBrowser.lastAction===p.expected''',
                arg={'before': before, 'expected': expected})

    def theme(self, index: int):
        self.click('theme')
        self.page.wait_for_function('window.chartSceneBrowser.themeDropDownOpen===true')
        self.page.keyboard.press('Home')
        self.page.wait_for_function('window.chartSceneBrowser.themeFocusedIndex===0')
        for position in range(index):
            self.page.keyboard.press('ArrowDown')
            self.page.wait_for_function('i=>window.chartSceneBrowser.themeFocusedIndex===i', arg=position+1)
        self.page.keyboard.press('Enter')
        self.page.wait_for_function('window.chartSceneBrowser.themeDropDownOpen===false')
