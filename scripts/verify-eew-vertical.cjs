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
    for (const node of panel.querySelectorAll('.vertical-eew-title,.vertical-eew-message,.vertical-eew-areas,.vertical-eew-instruction,.vertical-eew-estimates')) {
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
run('changed warning areas are orange in both layouts', () => {
  const labels = first.areas.map(name => ({ name, isChanged: name === '新潟' || name === '長野' }));
  const reported = warning(first).map(block => block.eewCard || block.styleToken === 'eew-areas'
    ? { ...block, eewAreaLabels: labels } : block);
  for (const vertical of [false, true]) {
    applySnapshot({ ...base, useVerticalEewWarningLayout: vertical, blocks: reported });
    if (vertical) checkFit();
    const areas = [...blocks.querySelectorAll(vertical ? '.vertical-eew-area' : '.eew-areas .primary span')];
    assert(JSON.stringify(areas.map(area => area.textContent)) === JSON.stringify(first.areas), 'Region names or order changed');
    for (const [index, area] of areas.entries()) {
      const expected = labels[index].isChanged ? 'rgb(255, 165, 0)' : horizontalStyle.foreground;
      assert(getComputedStyle(area).color === expected, 'Unexpected color for ' + area.textContent);
    }
    if (!vertical) assert(blocks.querySelector('.eew-areas .primary').textContent === first.areas.join('　'), 'Horizontal spacing changed');
  }
  reported[2].primaryText = '手動で編集した地域名';
  applySnapshot({ ...base, useVerticalEewWarningLayout: false, blocks: reported });
  assert(blocks.querySelector('.eew-areas .primary').textContent === reported[2].primaryText &&
    !blocks.querySelector('.eew-area-changed'), 'Highlight replaced edited text');
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
run('estimates are attached to their own warning in both layouts', () => {
  const firstDetails = '予想最大震度 7　推定マグニチュード 9.9';
  const secondDetails = '予想最大震度 5弱〜5強　推定マグニチュード 6.6';
  const firstBlocks = warning(first);
  const secondBlocks = warning(second);
  firstBlocks[2].eewEstimateText = firstDetails;
  secondBlocks[2].eewEstimateText = secondDetails;
  const combined = [...firstBlocks, ...secondBlocks];
  applySnapshot({ ...base, useVerticalEewWarningLayout: false, blocks: combined });
  const horizontal = [...blocks.querySelectorAll('.eew-areas .eew-estimates')].map(node => node.textContent);
  assert(horizontal[0] === firstDetails && horizontal[1] === secondDetails, 'Horizontal estimates missing or mixed');
  applySnapshot({ ...base, blocks: combined });
  const vertical = checkFit().map(node => node.querySelector('.vertical-eew-estimates')?.textContent);
  assert(vertical[0] === firstDetails && vertical[1] === secondDetails, 'Vertical estimates missing or mixed');
  const cancel = card('先ほどの、緊急地震速報を取り消します', [], true);
  applySnapshot({ ...base, blocks: [...warning(cancel), ...secondBlocks] });
  const panels = checkFit();
  assert(!panels[0].querySelector('.vertical-eew-estimates'), 'Cancelled warning still shows estimates');
  assert(panels[1].querySelector('.vertical-eew-estimates')?.textContent === secondDetails, 'Other warning lost estimates');
  applySnapshot(base);
  assert(!blocks.querySelector('.vertical-eew-estimates'), 'An omitted estimate left its row visible');
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
run('left cancellation keeps the training label and the other warning visible', () => {
  const cancel = card('先ほどの、緊急地震速報を取り消します', [], true);
  applySnapshot({ ...base, alignVerticalEewWarningsLeft: true,
    rehearsalLabel: '訓練・試験電文 実際の地震ではありません', blocks: [...warning(cancel), ...warning(first)] });
  const panels = checkFit();
  assert(!banner.hidden && banner.textContent.includes('訓練'), 'Training banner hidden');
  assert(panels[0].getBoundingClientRect().top > banner.getBoundingClientRect().bottom, 'Training banner overlaps');
  assert(panels[0].classList.contains('cancelled') && panels[0].textContent.includes('取り消します'), 'Cancel lost');
  assert(panels[1].textContent.includes('石川'), 'Other warning lost');
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
function caption(styleToken, badge, body, hideBadge = false, secondaryText = "") {
  const subtitleBodyText = hideBadge && badge ? body : null;
  return { styleToken, badge, hideBadge, subtitleBodyText, secondaryText,
    primaryText: subtitleBodyText === null ? body : badge + '　' + body };
}
function checkCaptionColumns(rows, badgeCount, plainLabels) {
  applySnapshot({ ...base, blocks: rows });
  const texts = [...blocks.querySelectorAll('.texts .primary')];
  assert(texts.length === rows.length, 'Missing subtitle rows');
  const left = texts[0].getBoundingClientRect().left;
  for (const [index, text] of texts.entries()) {
    assert(Math.abs(text.getBoundingClientRect().left - left) < 1, 'Subtitle columns are not aligned');
    assert(text.textContent === (rows[index].subtitleBodyText ?? rows[index].primaryText), 'Subtitle changed');
  }
  assert(blocks.querySelectorAll('.badge:not(.placeholder)').length === badgeCount, 'Badge visibility changed');
  const labels = [...blocks.querySelectorAll('.subtitle-label')].map(label => label.textContent).filter(Boolean);
  assert(JSON.stringify(labels) === JSON.stringify(plainLabels), 'Severity text changed');
}
run('earthquake captions align across different badge widths', () => {
  checkCaptionColumns([caption('intensity', '震度5弱以上 未入電', '石川県輪島市'),
    caption('intensity', '震度3', '富山県富山市')], 2, []);
});
function checkCaptionBadgeCenter(style, label, body, note = '') {
  for (const [fontScale, lineSpacing, subtitle, wraps] of [
    [1, 1, body, false],
    [1.2, 1.25, (body + '　').repeat(9), true]
  ]) {
    applySnapshot({ ...base, fontScale, lineSpacing, blocks: [caption(style, label, subtitle, false, note)] });
    const badge = blocks.querySelector('.badge').getBoundingClientRect();
    const text = blocks.querySelector('.texts .primary');
    const scale = new DOMMatrixReadOnly(getComputedStyle(canvas).transform).a;
    const lineHeight = parseFloat(getComputedStyle(text).lineHeight) * scale;
    const box = text.getBoundingClientRect();
    assert((box.height > lineHeight * 1.5) === wraps, style + ': unexpected subtitle wrapping');
    const offset = Math.abs((badge.top + badge.height / 2) - (box.top + lineHeight / 2));
    assert(offset < 0.75, style + ': badge and first line differ by ' + offset.toFixed(2) + 'px');
  }
}
run('intensity badge is centered on the first location line', () => {
  checkCaptionBadgeCenter('intensity', '震度4', '石川県能登');
});
run('tsunami badge is centered on the first subtitle line', () => {
  checkCaptionBadgeCenter('tsunami', '津波情報', '津波情報が変更されました');
  checkCaptionBadgeCenter('tsunami', '津波注意報', '岩手県沿岸北部', '〔1m〕');
});
run('volcano badges are centered on the first subtitle line', () => {
  checkCaptionBadgeCenter('volcano-forecast', '噴火予報', '桜島の噴火予報');
  checkCaptionBadgeCenter('volcano-warning', '噴火警報', '桜島の火口周辺に警戒');
  checkCaptionBadgeCenter('eruption-flash', '噴火速報', '桜島で噴火が発生しました');
});
run('earthquake plain severity keeps continuation rows aligned', () => {
  checkCaptionColumns([caption('intensity', '震度5弱', '石川県輪島市', true),
    caption('intensity', '', '石川県珠洲市', true)], 0, ['震度5弱']);
});
run('tsunami captions align with or without badges', () => {
  for (const hideBadge of [false, true]) {
    checkCaptionColumns([caption('tsunami', '津波注意報', '岩手県', hideBadge, '〔1m〕'),
      caption('tsunami', '', '宮城県', hideBadge, '〔1m〕')], hideBadge ? 0 : 1, hideBadge ? ['津波注意報'] : []);
  }
});
run('overseas tsunami heights use the longest location on each page', () => {
  const heading = { styleToken: 'comment', primaryText: '海外の津波観測' };
  const row = (primaryText, secondaryText) => ({ styleToken: 'overseas-tsunami-observation', primaryText, secondaryText });
  const short = row('チリ・コラル', '0.9 m');
  const long = row('仏領マルキーズ諸島・ヒバオア島', '1.8 m');
  const textBox = node => {
    const range = document.createRange();
    range.selectNodeContents(node);
    return range.getBoundingClientRect();
  };
  for (const rows of [[short], [short, long], [short]]) {
    applySnapshot({ ...base, blocks: [heading, ...rows] });
    const labels = [...blocks.querySelectorAll('.overseas-tsunami-observation .primary')].map(textBox);
    const heights = [...blocks.querySelectorAll('.overseas-tsunami-observation .secondary')].map(textBox);
    assert(heights.length === rows.length, 'Missing tsunami observation');
    assert(Math.abs(heights[0].top - labels[0].top) < 1, 'Location and height are on different lines');
    const gap = heights[0].left - Math.max(...labels.map(label => label.right));
    assert(gap >= 20 && gap <= 38, 'Unexpected gap after the longest location: ' + gap.toFixed(2) + 'px');
    if (rows.length > 1) {
      assert(heights[0].left === heights[1].left && heights[1].top > heights[0].bottom, 'Heights do not align across rows');
    }
  }
  applySnapshot({ ...base, blocks: [heading] });
  assert(!blocks.classList.contains('overseas-tsunami-observations'), 'Observation layout applied to narrative page');
});
run('weather area headings retain their position beside the badge', () => {
  applySnapshot({ ...base, blocks: [{ ...caption('weather-warning', '大雨警報', '千代田区'), weatherHeading: '東京都｜発表' }] });
  const badge = blocks.querySelector('.badge').getBoundingClientRect();
  const heading = blocks.querySelector('.weather-context').getBoundingClientRect();
  const places = blocks.querySelector('.texts .primary').getBoundingClientRect();
  assert(heading.left > badge.right && places.left === badge.left && places.top >= heading.bottom, 'Weather heading moved');
  assert(!blocks.classList.contains('aligned-labels'), 'Earthquake and tsunami columns applied to weather');
});
async function checkSubtitleImage() {
  const logo = document.getElementById('subtitleImage');
  const loaded = new Promise((resolve, reject) => {
    logo.addEventListener('load', resolve, { once: true });
    logo.addEventListener('error', () => reject(new Error('PNG did not load')), { once: true });
  });
  const withImage = { ...base, subtitleImage: { id: 'a'.repeat(64), x: 24, y: 780, width: 156 } };
  applySnapshot(withImage);
  await loaded;
  assert(!logo.hidden && logo.width === 156 && logo.height === 156, 'PNG is not visible at the specified width and aspect ratio');
  assert(logo.offsetLeft === 24 && logo.offsetTop === 780, 'PNG is not at the specified position');
  applySnapshot({ ...withImage, hasProgram: false });
  assert(logo.hidden, 'PNG remained after caption disappeared');
  applySnapshot(withImage);
  assert(!logo.hidden, 'PNG did not return with its caption');
  applySnapshot(base);
  assert(logo.hidden, 'PNG carried over to a caption without an image');
}

checkSubtitleImage().then(
  () => results.push({ name: 'PNG follows caption visibility and placement', passed: true }),
  error => results.push({ name: 'PNG follows caption visibility and placement', passed: false, error: error.message })
).finally(() => {
  // Leave a representative two-warning frame for the screenshot.
  applySnapshot({ ...base, alignVerticalEewWarningsLeft: ${leftPreview}, blocks: [...warning(first), ...warning(second)] });
  const result = document.createElement('pre'); result.id = 'probe-result'; result.hidden = true;
  result.textContent = JSON.stringify(results); document.body.appendChild(result);
});
</script>`;

const server = http.createServer((request, response) => {
  if (request.url === "/monitor/view") {
    response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    response.end(html.replace("</body>", probe + "</body>"));
  } else if (request.url === "/assets/overlay.js") {
    response.writeHead(200, { "Content-Type": "text/javascript; charset=utf-8" });
    response.end(script);
  } else if (request.url === `/subtitle-image/${"a".repeat(64)}?token=`) {
    response.writeHead(200, { "Content-Type": "image/png" });
    response.end(Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=", "base64"));
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
