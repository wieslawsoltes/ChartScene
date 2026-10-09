"""Exercise compiled Uno through real pointer/keyboard input. Diagnostic state is read-only."""
from __future__ import annotations
import argparse
import json
import math
from pathlib import Path
import time
import traceback
from playwright.sync_api import sync_playwright

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:8765/')
parser.add_argument('--output', default='artifacts/browser-tests')
parser.add_argument('--catalog', action='store_true')
parser.add_argument('--headed', action='store_true')
args = parser.parse_args()
out = Path(args.output)
out.mkdir(parents=True, exist_ok=True)
results: list[dict] = []
logs: list[dict] = []

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=not args.headed, args=[
        '--enable-webgl', '--ignore-gpu-blocklist', '--use-gl=angle',
        '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
    ])
    context = browser.new_context(viewport={'width': 1440, 'height': 1000}, device_scale_factor=1, accept_downloads=True)
    page = context.new_page()
    page.on('console', lambda message: logs.append({'type': message.type, 'text': message.text}))
    page.on('pageerror', lambda error: logs.append({'type': 'pageerror', 'text': str(error)}))
    page.on('requestfailed', lambda request: logs.append({'type': 'requestfailed', 'url': request.url, 'text': request.failure}))
    page.set_default_timeout(20_000)

    def snapshot() -> dict:
        return page.evaluate('window.chartScene || {}')

    def wait_ready(example: str | None = None) -> None:
        page.wait_for_function("""id => {
            const state = window.chartScene;
            if (state?.fatalError || state?.error) throw new Error(state.fatalError || state.error);
            return state?.ready && (!id || state.exampleId === id);
        }""", arg=example, timeout=120_000)

    def control(name: str) -> dict:
        page.wait_for_function("name => {const b=window.chartScene?.controls?.[name];return b && b.width>0 && b.height>0;}", arg=name)
        value = snapshot()['controls'][name]
        assert all(math.isfinite(value[k]) for k in ('x', 'y', 'width', 'height')), value
        return value

    def click(name: str) -> None:
        bounds = control(name)
        page.mouse.click(bounds['x'] + bounds['width'] / 2, bounds['y'] + bounds['height'] / 2)
        page.wait_for_timeout(120)

    def search(text: str) -> None:
        click('search')
        page.keyboard.press('Control+A')
        page.keyboard.insert_text(text)
        page.wait_for_timeout(350)

    def choose(identifier: str) -> None:
        state = snapshot()
        entry = next(item for item in state['catalog'] if item['Id'] == identifier)
        search(entry['Title'])
        click('example:' + identifier)
        wait_ready(identifier)
        page.wait_for_timeout(200)

    def plot_point(x: float, y: float) -> tuple[float, float]:
        state = snapshot()
        chart, plot = state['controls']['chart'], state['plot']
        return chart['x'] + plot['X'] + x * plot['Width'], chart['y'] + plot['Y'] + y * plot['Height']

    def test(name: str, action) -> bool:
        start = time.perf_counter()
        try:
            action()
            state = snapshot()
            assert not state.get('fatalError') and not state.get('error'), state
            results.append({'name': name, 'passed': True, 'milliseconds': (time.perf_counter()-start)*1000})
            print('PASS', name, flush=True)
            return True
        except Exception as error:
            results.append({'name': name, 'passed': False, 'error': str(error), 'trace': traceback.format_exc(), 'milliseconds': (time.perf_counter()-start)*1000})
            print('FAIL', name, str(error), flush=True)
            page.screenshot(path=str(out / (name.replace(' ', '-').replace('/', '-') + '-failure.png')))
            (out / 'failure-state.json').write_text(json.dumps(snapshot(), indent=2))
            return False
        finally:
            (out / 'results.json').write_text(json.dumps({'total': len(results), 'failed': sum(not r['passed'] for r in results), 'tests': results}, indent=2))
            (out / 'console.json').write_text(json.dumps(logs, indent=2))

    try:
        page.goto(args.url, wait_until='domcontentloaded', timeout=60_000)
        if not test('Uno application startup', lambda: wait_ready('overview')):
            raise RuntimeError('The compiled Uno application failed to start.')
        page.screenshot(path=str(out / 'overview.png'))
        (out / 'initial-state.json').write_text(json.dumps(snapshot(), indent=2))

        def wheel_zoom():
            before = snapshot()['ranges']['x']
            page.mouse.move(*plot_point(.35, .6))
            page.mouse.wheel(0, -240)
            page.wait_for_function('old => window.chartScene.ranges.x.max-window.chartScene.ranges.x.min < old', arg=before['max']-before['min'])
            after = snapshot()['ranges']['x']
            expected = before['min'] + .35 * (before['max']-before['min'])
            actual = after['min'] + .35 * (after['max']-after['min'])
            assert abs(actual-expected) < 1e-5 * max(1, abs(expected)), (actual, expected)
            assert snapshot()['canUndo']
        test('wheel zoom anchor', wheel_zoom)

        def undo_redo():
            zoomed = snapshot()['ranges']['x']
            click('undo')
            page.wait_for_function('old => window.chartScene.ranges.x.max-window.chartScene.ranges.x.min > old', arg=zoomed['max']-zoomed['min'])
            click('redo')
            page.wait_for_function('old => Math.abs(window.chartScene.ranges.x.min-old)<1e-7', arg=zoomed['min'])
        test('viewport undo redo buttons', undo_redo)

        def pan():
            click('home')
            before = snapshot()['ranges']['x']
            start, end = plot_point(.3, .7), plot_point(.45, .7)
            page.mouse.move(*start)
            page.mouse.down()
            page.mouse.move(*end, steps=8)
            page.mouse.up()
            page.wait_for_function('old => window.chartScene.ranges.x.min < old', arg=before['min'])
            after = snapshot()['ranges']['x']
            assert abs((after['max']-after['min'])-(before['max']-before['min'])) < 1e-7
        test('pointer drag pan', pan)

        def rectangle_zoom():
            click('home')
            click('zoom-rectangle')
            before = snapshot()['ranges']['x']
            page.mouse.move(*plot_point(.2, .3))
            page.mouse.down()
            page.mouse.move(*plot_point(.65, .7), steps=8)
            page.mouse.up()
            page.wait_for_function('old => window.chartScene.ranges.x.max-window.chartScene.ranges.x.min < old*.6', arg=before['max']-before['min'])
            click('pan')
        test('rectangle zoom mode', rectangle_zoom)

        def create_annotation():
            click('home')
            click('annotate')
            before = len(snapshot()['annotations'])
            page.mouse.move(*plot_point(.18, .22))
            page.mouse.down()
            page.mouse.move(*plot_point(.38, .46), steps=8)
            page.mouse.up()
            page.wait_for_function('n => window.chartScene.annotations.length === n+1', arg=before)
            annotation = snapshot()['annotations'][-1]
            assert annotation['X1'] != annotation['X2'] and annotation['Y1'] != annotation['Y2']
            click('undo')
            page.wait_for_function('n => window.chartScene.annotations.length === n', arg=before)
            click('redo')
            page.wait_for_function('n => window.chartScene.annotations.length === n+1', arg=before)
            click('pan')
        test('annotation creation undo redo', create_annotation)

        def themes():
            click('theme')
            page.keyboard.press('Home')
            page.keyboard.press('ArrowDown')
            page.keyboard.press('Enter')
            page.wait_for_function("window.chartScene.theme === 'Light'")
            page.screenshot(path=str(out / 'light-theme.png'))
            click('theme')
            page.keyboard.press('Home')
            page.keyboard.press('Enter')
            page.wait_for_function("window.chartScene.theme === 'Dark'")
        test('theme control', themes)

        def streaming():
            click('play')
            wait_ready('live')
            page.wait_for_function('window.chartScene.streaming === true')
            before = snapshot()
            page.wait_for_function('revision => window.chartScene.series[0].revision > revision+3', arg=before['series'][0]['revision'])
            state = snapshot()
            assert all(series['count'] == 2000 for series in state['series'])
            assert state['ranges']['x']['max'] > before['ranges']['x']['max']
            click('play')
            page.wait_for_function('window.chartScene.streaming === false')
            revision = snapshot()['series'][0]['revision']
            page.wait_for_timeout(300)
            assert snapshot()['series'][0]['revision'] == revision
            page.screenshot(path=str(out / 'streaming.png'))
        test('real-time FIFO stream and pause', streaming)

        def png_export():
            with page.expect_download(timeout=45_000) as download_info:
                click('export-png')
            download = download_info.value
            destination = out / download.suggested_filename
            download.save_as(destination)
            assert destination.read_bytes().startswith(b'\x89PNG\r\n\x1a\n')
            assert destination.stat().st_size > 5000
        test('actual PNG download', png_export)

        def csv_export():
            with page.expect_download(timeout=30_000) as download_info:
                click('export-csv')
            download = download_info.value
            destination = out / download.suggested_filename
            download.save_as(destination)
            lines = destination.read_text().splitlines()
            assert lines[0] == 'X,Y' and len(lines) == 2001, (lines[0], len(lines))
        test('actual CSV download', csv_export)

        def responsive():
            page.set_viewport_size({'width': 800, 'height': 700})
            page.wait_for_function('window.chartScene.width === 800')
            assert snapshot()['controls']['inspector-panel'] is None
            assert snapshot()['plot']['Width'] > 300
            page.screenshot(path=str(out / 'compact-layout.png'))
            page.set_viewport_size({'width': 1440, 'height': 1000})
            page.wait_for_function('window.chartScene.width === 1440')
        test('responsive compact layout', responsive)

        if args.catalog:
            for entry in snapshot()['catalog']:
                identifier = entry['Id']
                def validate_example(identifier=identifier):
                    choose(identifier)
                    state = snapshot()
                    assert state['statistics']['RenderedPoints'] > 0
                    assert not state.get('error')
                    if identifier in {'overview','million','financial','heatmap','vectorfield','polarline','ternaryscatter','bar','annotations'}:
                        page.screenshot(path=str(out / (identifier + '.png')))
                test('catalog ' + identifier, validate_example)

        def no_unhandled_errors():
            errors = [item for item in logs if item['type'] == 'pageerror']
            assert not errors, errors
            assert not snapshot().get('hostErrors'), snapshot().get('hostErrors')
        test('no unhandled browser errors', no_unhandled_errors)
        (out / 'final-state.json').write_text(json.dumps(snapshot(), indent=2))
    finally:
        (out / 'console.json').write_text(json.dumps(logs, indent=2))
        browser.close()

failed = sum(not result['passed'] for result in results)
print(f'{len(results)-failed}/{len(results)} browser tests passed', flush=True)
raise SystemExit(1 if failed else 0)
