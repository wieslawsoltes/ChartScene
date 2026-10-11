"""Cold-start the compiled Uno galleries without any mouse/keyboard or app-state writes."""
from __future__ import annotations
import argparse
import asyncio
from io import BytesIO
import json
from pathlib import Path
import re
import time
import traceback
from PIL import Image
from playwright.async_api import async_playwright

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:8765/ChartScene/')
parser.add_argument('--output', default='artifacts/browser-tests-startup')
parser.add_argument('--repeats', type=int, default=3)
args = parser.parse_args()
if not 1 <= args.repeats <= 20: parser.error('--repeats must be between 1 and 20')
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
results, logs = [], []

async def main():
    async with async_playwright() as p:
        browser = await p.chromium.launch(args=['--enable-webgl','--ignore-gpu-blocklist','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader'])
        globals_by_view = {'2d':'chartScene', '3d':'chartScene3D', 'linked':'chartSceneLinked', 'colors':'chartSceneColors', 'overview':'chartSceneOverview', 'stacks':'chartSceneStacks'}
        base_cases = [(name, 0) for name in globals_by_view] + [('stacks',250), ('stacks',700)]
        cases = [(view,delay,attempt) for attempt in range(args.repeats) for view,delay in base_cases]
        try:
            for view, font_delay, attempt in cases:
                name = f'{view} no-input startup font-delay-{font_delay}ms attempt-{attempt+1}'
                slug = f'{view}-{font_delay}-{attempt+1}'
                started = time.perf_counter(); observations = []
                context = await browser.new_context(viewport={'width':1440,'height':1000})
                page = await context.new_page(); page.set_default_timeout(20_000)
                errors = []; delayed_fonts = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.on('console', lambda message, view=view: logs.append({'view':view,'type':message.type,'text':message.text}))
                if font_delay:
                    async def delay_font(route):
                        delayed_fonts.append(route.request.url)
                        await asyncio.sleep(font_delay / 1000)
                        await route.continue_()
                    await context.route(re.compile(r'\.(?:woff2?|ttf|otf)(?:\?.*)?$', re.I), delay_font)
                try:
                    await page.goto(args.url + '?view=' + view, wait_until='domcontentloaded')
                    await page.wait_for_function('''key => {
                        const fatal=window.chartScene?.fatalError;
                        if(fatal)throw new Error(fatal);
                        const s=window[key];if(s?.error)throw new Error(s.error);
                        return s?.ready && window.chartSceneStartup?.sessions===1 && window.chartSceneStartup.active===false;
                    }''', arg=globals_by_view[view], timeout=120_000)
                    # A colored loader progress bar must never count as a presented chart.
                    # Observe its removal by Uno after presentation; never remove it here.
                    await page.wait_for_function("!document.querySelector('#loading.uno-persistent-loader')")
                    if font_delay: assert delayed_fonts, 'The delayed-font fixture must intercept actual font requests.'
                    deadline = time.monotonic() + 8; consecutive = 0
                    while time.monotonic() < deadline:
                        state = await page.evaluate('key=>window[key]', globals_by_view[view])
                        image_bytes = await page.screenshot()
                        with Image.open(BytesIO(image_bytes)) as image:
                            rgb = image.convert('RGB')
                            if view == 'stacks':
                                b = state['plot']
                                rgb = rgb.crop((int(b['x']),int(b['y']),int(b['x']+b['width']),int(b['y']+b['height'])))
                            colored = sum(max(c)-min(c)>45 and max(c)>100 for c in rgb.resize((300,180)).getdata())
                        observations.append({'coloredPixels':colored,'elapsedMs':(time.perf_counter()-started)*1000})
                        consecutive = consecutive+1 if colored > (500 if view == 'stacks' else 50) else 0
                        if consecutive >= 2:
                            (out / f'{slug}.png').write_bytes(image_bytes)
                            break
                        await asyncio.sleep(.1)
                    assert consecutive >= 2, observations
                    if view == 'stacks':
                        before = await page.evaluate('window.chartSceneStacks.frames')
                        await asyncio.sleep(1)
                        after = await page.evaluate('window.chartSceneStacks.frames')
                        assert after-before <= 3, ('Unexpected continuous redraw',before,after)
                    startup = await page.evaluate('window.chartSceneStartup')
                    assert startup['qualifiedUno']=='6.7.135' and startup['sessions']==1 and startup['active'] is False and startup['detached'] is False, startup
                    assert isinstance(startup['compositionCallbacks'],int) and startup['compositionCallbacks']>=0, startup
                    assert not errors, errors
                    results.append({'name':name,'passed':True,'milliseconds':(time.perf_counter()-started)*1000,'delayedFontRequests':len(delayed_fonts),'startup':startup,'observations':observations})
                    print('PASS',name,flush=True)
                except Exception as error:
                    results.append({'name':name,'passed':False,'error':str(error),'trace':traceback.format_exc(),'observations':observations})
                    print('FAIL',name,error,flush=True)
                    await page.screenshot(path=str(out / f'{slug}-failure.png'))
                    state = await page.evaluate('key=>({gallery:window[key],startup:window.chartSceneStartup,fatal:window.chartScene?.fatalError,loader:document.querySelector("#loading")?.outerHTML})', globals_by_view[view])
                    (out / f'{slug}-failure.json').write_text(json.dumps(state,indent=2))
                finally:
                    (out / 'results.json').write_text(json.dumps({'total':len(results),'failed':sum(not r['passed'] for r in results),'tests':results},indent=2))
                    (out / 'console.json').write_text(json.dumps(logs,indent=2))
                    await context.close()
        finally:
            await browser.close()

asyncio.run(main())
failed = sum(not r['passed'] for r in results)
print(f'{len(results)-failed}/{len(results)} no-input startup checks passed',flush=True)
raise SystemExit(1 if failed else 0)
