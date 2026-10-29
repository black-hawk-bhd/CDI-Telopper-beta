"use strict";

// Browser rendering regression test; no live receiver, user profile or OBS is used.
// Run from the repository root: node scripts/verify-eew-vertical.cjs
// Set CDI_CHROME_EXE if Chrome is installed at a nonstandard location.
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { spawn } = require("node:child_process");

const root = path.resolve(__dirname, "..");
const leftPreview = process.argv.includes("--left");
const output = path.join(root, "artifacts", "validation", "eew-vertical");
fs.mkdirSync(output, { recursive: true });
const profile = fs.mkdtempSync(path.join(output, "chrome-profile-"));
const chrome = process.env.CDI_CHROME_EXE || "C:/Program Files/Google/Chrome/Application/chrome.exe";
const assets = path.join(root, "src", "EEWTelop.Wpf", "Obs", "Assets");
const script = fs.readFileSync(path.join(assets, "overlay.js"));
const html = fs.readFileSync(path.join(assets, "overlay.html"), "utf8");
const probe = `
<style>body { background: repeating-conic-gradient(#364250 0 25%, #2d3743 0 50%) 0 / 32px 32px; }</style>
<script>
const results = [];
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const card = (message, areas, cancelled = false) => ({ title: cancelled ? '緊急地震速報（取消）' : '緊急地震速報（気象庁）',
  message, instruction: cancelled ? '' : '強い揺れに警戒', areas, isCancelled: cancelled });
const warning = value => [
  { styleToken: value.isCancelled ? 'eew-header-cancel' : 'eew-header', primaryText: value.title, eewCard: value },
  { styleToken: 'eew-warning', primaryText: value.message },
  { styleToken: 'eew-areas', primaryText: value.areas.join('　') }
];
const first = card('能登半島沖で地震', ['石川','新潟','富山','長野']);
const second = card('富山湾で地震', []);
const base = { hasProgram: true, backgroundMode: 'Transparent', fontScale: 1, lineSpacing: 1,
  letterSpacingEm: 0, useVerticalEewWarningLayout: true, blocks: warning(first) };
let horizontalStyle;
function checkFit() {
  const nodes = [...document.querySelectorAll('.vertical-eew-card')];
  for (const panel of nodes) {
    const box = panel.getBoundingClientRect();
    assert(box.left >= 0 && box.right <= 1921 && box.top >= 0 && box.bottom <= 1081, 'Panel outside canvas');
    for (const node of panel.querySelectorAll('.vertical-eew-title,.vertical-eew-message,.vertical-eew-areas,.vertical-eew-instruction')) {
      assert(node.clientWidth > 0 && node.clientHeight > 0, 'Zero-sized content');
      assert(node.scrollWidth <= node.clientWidth + 1 && node.scrollHeight <= node.clientHeight + 1,
        'Overflow: ' + node.className + ' ' + node.textContent);
    }
  }
  return nodes;
}
function run(name, action) {
  try { action(); results.push({ name, passed: true }); }
  catch (error) { results.push({ name, passed: false, error: error.message }); }
}
run('horizontal default unchanged', () => {
  applySnapshot({ ...base, useVerticalEewWarningLayout: false, alignVerticalEewWarningsLeft: true });
  assert(!content.classList.contains('vertical') && blocks.children.length === 3, 'Not horizontal');
  assert(!content.classList.contains('left-aligned'), 'Horizontal alignment changed');
  const title = getComputedStyle(blocks.querySelector('.eew-header'));
  const primary = getComputedStyle(blocks.querySelector('.eew-areas .primary'));
  horizontalStyle = { background: getComputedStyle(content).backgroundColor, header: title.backgroundColor,
    warning: getComputedStyle(blocks.querySelector('.eew-warning .primary')).color,
    foreground: primary.color, family: primary.fontFamily, weight: primary.fontWeight, shadow: primary.textShadow };
  assert(horizontalStyle.background === 'rgb(58, 58, 208)' && horizontalStyle.header === 'rgb(168, 50, 24)', 'Horizontal palette changed');
  assert(horizontalStyle.warning === 'rgb(240, 240, 0)' && horizontalStyle.foreground === 'rgb(255, 255, 255)', 'Horizontal text color changed');
  assert(primary.fontSize === '58px' && primary.fontWeight === '900', 'Horizontal font changed');
});
run('single vertical warning', () => {
  applySnapshot(base);
  assert(checkFit().length === 1, 'Missing card');
  assert(Math.abs(checkFit()[0].getBoundingClientRect().right - content.getBoundingClientRect().right) < 1, 'Default is not right aligned');
  assert(document.querySelectorAll('.vertical-eew-area').length === 4, 'Missing regions');
  assert(getComputedStyle(document.querySelector('.vertical-eew-areas')).writingMode === 'vertical-rl', 'Not vertical writing');
});
run('vertical appearance follows existing telop palette and font', () => {
  applySnapshot(base);
  const panel = getComputedStyle(document.querySelector('.vertical-eew-card'));
  const title = getComputedStyle(document.querySelector('.vertical-eew-title'));
  const message = getComputedStyle(document.querySelector('.vertical-eew-message'));
  const areas = getComputedStyle(document.querySelector('.vertical-eew-areas'));
  assert(panel.backgroundColor === horizontalStyle.background && title.backgroundColor === horizontalStyle.header, 'Different background/header colors');
  assert(message.color === horizontalStyle.warning && areas.color === horizontalStyle.foreground, 'Different text colors');
  for (const style of [title, message, areas]) {
    assert(style.fontFamily === horizontalStyle.family && style.fontWeight === horizontalStyle.weight, 'Different font');
    assert(style.textShadow === horizontalStyle.shadow, 'Different text shadow');
  }
  assert(panel.borderTopWidth === '0px' && panel.outlineWidth === '0px' && panel.boxShadow === 'none',
    'Extra frame/shadow: ' + [panel.borderTopWidth, panel.outlineWidth, panel.boxShadow].join(', '));
  assert(document.querySelector('.vertical-eew-title').textContent === '緊急地震速報（気象庁）', 'Attribution missing from heading');
  assert(document.querySelector('.vertical-eew-instruction').textContent === first.instruction, 'Instruction changed');
});
run('two independent warnings', () => {
  applySnapshot({ ...base, blocks: [...warning(first), ...warning(second)] });
  const panels = checkFit();
  assert(panels.length === 2, 'Missing concurrent card');
  assert(panels[0].textContent.includes('石川') && !panels[1].textContent.includes('石川'), 'Regions mixed');
  assert(panels[0].getBoundingClientRect().right < panels[1].getBoundingClientRect().left, 'Cards overlap');
});
run('single warning switches left and back to right', () => {
  applySnapshot({ ...base, alignVerticalEewWarningsLeft: true });
  assert(Math.abs(checkFit()[0].getBoundingClientRect().left - content.getBoundingClientRect().left) < 1, 'Single card not left aligned');
  applySnapshot({ ...base, alignVerticalEewWarningsLeft: false });
  assert(!content.classList.contains('left-aligned'), 'Left class not cleared');
  assert(Math.abs(checkFit()[0].getBoundingClientRect().right - content.getBoundingClientRect().right) < 1, 'Right alignment not restored');
});
run('left alignment moves the group without changing its contents or inner layout', () => {
  const state = { ...base, blocks: [...warning(first), ...warning(second)] };
  applySnapshot(state);
  const before = checkFit().map(panel => ({ text: panel.textContent, width: panel.clientWidth,
    titleOffset: panel.querySelector('.vertical-eew-title').getBoundingClientRect().left - panel.getBoundingClientRect().left }));
  applySnapshot({ ...state, alignVerticalEewWarningsLeft: true });
  const panels = checkFit();
  assert(Math.abs(panels[0].getBoundingClientRect().left - content.getBoundingClientRect().left) < 1, 'Group not left aligned');
  assert(panels[0].getBoundingClientRect().right < panels[1].getBoundingClientRect().left, 'Left cards overlap');
  panels.forEach((panel, index) => {
    assert(panel.textContent === before[index].text && panel.clientWidth === before[index].width, 'Card changed');
    const titleOffset = panel.querySelector('.vertical-eew-title').getBoundingClientRect().left - panel.getBoundingClientRect().left;
    assert(Math.abs(titleOffset - before[index].titleOffset) < 1, 'Inner layout changed');
    assert(getComputedStyle(panel).backgroundColor === horizontalStyle.background, 'Color changed');
  });
});
run('cancellation replaces its card only', () => {
  const cancel = card('先ほどの、緊急地震速報を取り消します', [], true);
  applySnapshot({ ...base, blocks: [...warning(cancel), ...warning(first)] });
  const panels = checkFit();
  assert(panels.length === 2 && panels[0].classList.contains('cancelled'), 'Cancellation missing');
  assert(panels[0].textContent.includes('取り消します') && !panels[0].textContent.includes('石川'), 'Stale cancelled region');
  assert(panels[1].textContent.includes('石川'), 'Other warning lost');
  assert(getComputedStyle(panels[0]).backgroundColor === horizontalStyle.background, 'Cancellation body color differs');
  assert(getComputedStyle(panels[0].querySelector('.vertical-eew-title')).backgroundColor === 'rgb(85, 85, 85)', 'Cancellation heading not gray');
});
run('left cancellation and training label stay separate from the page number', () => {
  const cancel = card('先ほどの、緊急地震速報を取り消します', [], true);
  applySnapshot({ ...base, alignVerticalEewWarningsLeft: true, pageIndicator: '1 / 2',
    rehearsalLabel: '訓練・試験電文 実際の地震ではありません', blocks: [...warning(cancel), ...warning(first)] });
  const panels = checkFit();
  assert(!banner.hidden && banner.textContent.includes('訓練'), 'Training banner hidden');
  assert(panels[0].getBoundingClientRect().top > banner.getBoundingClientRect().bottom, 'Training banner overlaps');
  assert(panels[0].classList.contains('cancelled') && panels[0].textContent.includes('取り消します'), 'Cancel lost');
  assert(panels[1].textContent.includes('石川'), 'Other warning lost');
  assert(!pageIndicator.hidden && pageIndicator.getBoundingClientRect().left > panels[1].getBoundingClientRect().right,
    'Page number overlaps left cards');
});
run('forecast and legacy snapshots fall back to horizontal', () => {
  const forecast = warning(first).map(({ eewCard, ...block }) => block);
  applySnapshot({ ...base, alignVerticalEewWarningsLeft: true, blocks: forecast });
  assert(!content.classList.contains('vertical') && blocks.children.length === 3, 'Forecast lost');
  assert(!content.classList.contains('left-aligned'), 'Forecast alignment changed');
});
run('training banner remains visible and separate', () => {
  applySnapshot({ ...base, rehearsalLabel: '訓練・試験電文 実際の地震ではありません' });
  assert(!banner.hidden && banner.textContent.includes('訓練'), 'Training banner hidden');
  assert(checkFit()[0].getBoundingClientRect().top > banner.getBoundingClientRect().bottom, 'Banner overlap');
});
run('many regions and long names are not omitted', () => {
  const names = Array.from({ length: 70 }, (_, index) => '検証対象地域' + String(index + 1));
  const long = card('長い震源地名と空白 A｜B\\n列島近海で地震', names);
  for (const left of [false, true]) {
    applySnapshot({ ...base, alignVerticalEewWarningsLeft: left, blocks: warning(long) });
    checkFit();
    const actual = [...document.querySelectorAll('.vertical-eew-area')].map(node => node.textContent);
    assert(JSON.stringify(actual) === JSON.stringify(names), 'Regions omitted or changed');
    assert(document.querySelector('.vertical-eew-message').textContent === long.message, 'Hypocenter changed');
  }
});
run('markup remains literal text', () => {
  const literal = card('<img src=x onerror=alert(1)>で地震', ['<script>bad</' + 'script>']);
  applySnapshot({ ...base, blocks: warning(literal) });
  checkFit();
  assert(!blocks.querySelector('img,script'), 'Markup interpreted');
  assert(blocks.textContent.includes('<img'), 'Text changed');
});
run('non-EEW output and empty state unchanged', () => {
  applySnapshot({ ...base, alignVerticalEewWarningsLeft: true, blocks: [{ styleToken: 'summary', primaryText: '地震情報' }] });
  assert(content.className === 'subtitle', 'Non-EEW layout changed');
  applySnapshot({ ...base, hasProgram: false });
  assert(content.hidden, 'Empty output remains visible');
});
// Leave a representative two-warning frame for the screenshot.
applySnapshot({ ...base, alignVerticalEewWarningsLeft: ${leftPreview}, blocks: [...warning(first), ...warning(second)] });
const result = document.createElement('pre'); result.id = 'probe-result'; result.hidden = true;
result.textContent = JSON.stringify(results); document.body.appendChild(result);
</script>`;

const server = http.createServer((request, response) => {
  if (request.url === "/monitor/view") {
    response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    response.end(html.replace("</body>", probe + "</body>"));
  } else if (request.url === "/assets/overlay.js") {
    response.writeHead(200, { "Content-Type": "text/javascript; charset=utf-8" });
    response.end(script);
  } else { response.writeHead(404); response.end(); }
});

server.listen(0, "127.0.0.1", () => {
  const screenshot = path.join(output, leftPreview ? "concurrent-warnings-left.png" : "concurrent-warnings.png");
  const child = spawn(chrome, ["--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
    "--disable-background-networking", "--disable-sync", "--disable-extensions", "--hide-scrollbars",
    "--window-size=1920,1080", "--force-device-scale-factor=1", "--user-data-dir=" + profile,
    "--virtual-time-budget=1000", "--screenshot=" + screenshot, "--dump-dom",
    "http://127.0.0.1:" + server.address().port + "/monitor/view"], { windowsHide: true });
  let stdout = "";
  let stderr = "";
  child.stdout.on("data", chunk => { stdout += chunk; });
  child.stderr.on("data", chunk => { stderr += chunk; });
  const timer = setTimeout(() => child.kill(), 45000);
  child.on("error", error => { console.error(error.message); server.close(); process.exitCode = 1; });
  child.on("close", code => {
    clearTimeout(timer);
    server.close();
    const match = stdout.match(/<pre id="probe-result" hidden="">([\s\S]*?)<\/pre>/);
    if (code !== 0 || !match) {
      console.error(JSON.stringify({ code, error: "Browser probe did not complete", stderr: stderr.slice(-1000) }));
      process.exitCode = 1;
      return;
    }
    const results = JSON.parse(match[1].replaceAll("&lt;", "<").replaceAll("&gt;", ">").replaceAll("&amp;", "&"));
    fs.writeFileSync(path.join(output, "results.json"), JSON.stringify(results, null, 2));
    for (const item of results) console.log((item.passed ? "PASS " : "FAIL ") + item.name + (item.error ? ": " + item.error : ""));
    console.log("Screenshot: " + screenshot);
    if (results.some(item => !item.passed)) process.exitCode = 1;
  });
});
