"use strict";
const token = new URLSearchParams(location.search).get("token") ?? "";
const endpoint = path => `${path}${path.includes("?") ? "&" : "?"}token=${encodeURIComponent(token)}`;
const kinds = { Quake: "地震", Eew: "EEW", Tsunami: "津波", WeatherWarning: "気象", Volcano: "火山" };
const source = mode => mode === "Production" ? "本番受信" : mode === "HistoryRehearsal" ? "過去情報／訓練" : "訓練";
const connectionNames = {Connected:"接続済み",Stopped:"停止中",Connecting:"接続中",Reconnecting:"再接続中",Stale:"通信停滞",Faulted:"接続異常",Unavailable:"状態不明"};
const time = value => new Date(value).toLocaleString("ja-JP");
const tiles = new Map();
const grid = document.getElementById("grid");
for (const [key, title] of Object.entries({general:"地震・火山",eew:"緊急地震速報",tsunami:"津波",weather:"気象"})) {
  const tile = document.createElement("section"); tile.className = "tile";
  const button = document.createElement("button"); button.type = "button";
  const label = document.createElement("span"); label.textContent = title;
  const state = document.createElement("small"); state.textContent = "待機中";
  button.append(label, state); button.setAttribute("aria-expanded", "false");
  button.addEventListener("click", () => button.setAttribute("aria-expanded", String(tile.classList.toggle("expanded"))));
  const screen = document.createElement("div"); screen.className = "screen";
  const frame = document.createElement("iframe"); frame.title = title + "の出力確認"; frame.src = endpoint("/monitor/view");
  screen.append(frame); tile.append(button, screen); grid.append(tile); tiles.set(key, {tile,frame,state});
}
const reviewFrame = document.getElementById("review-frame"); reviewFrame.src = endpoint("/monitor/view");
let telegrams = [], selected = "", pages = [], page = 0, generation = 0, listKey = "";
let latestState = null;
function send(frame, state) { frame.contentWindow?.postMessage({type:"cdi-monitor",state}, location.origin); }
function showChannels(data) {
  for (const [key, tile] of tiles) {
    const state = data.channels[key];
    tile.state.textContent = state.hasProgram ? `${source(state.sourceMode)}・${state.pageIndex + 1}/${state.pageCount}` : "待機／表示終了";
    tile.state.className = state.hasProgram && state.sourceMode !== "Production" ? "training" : "";
    send(tile.frame, state);
  }
}
for (const tile of tiles.values()) tile.frame.addEventListener("load", () => { if(latestState) showChannels(latestState); });
function showPage() {
  document.getElementById("page").textContent = pages.length ? `${page + 1} / ${pages.length}` : "";
  document.getElementById("previous").disabled = page <= 0;
  document.getElementById("next").disabled = page >= pages.length - 1;
  if (pages[page]) send(reviewFrame, pages[page]);
}
reviewFrame.addEventListener("load", showPage);
document.getElementById("previous").addEventListener("click", () => { if(page > 0) {page--;showPage();} });
document.getElementById("next").addEventListener("click", () => { if(page < pages.length-1) {page++;showPage();} });
function renderList() {
  const list = document.getElementById("list"); list.replaceChildren();
  const filter = document.getElementById("filter").value;
  for (const item of telegrams.filter(item => !filter || item.kind === filter)) {
    const button = document.createElement("button"); button.type = "button";
    button.textContent = `${time(item.receivedAt)} ${source(item.sourceMode)} ${kinds[item.kind] ?? item.kind} ${item.pages}ページ`;
    const summary = document.createElement("small"); summary.textContent = item.summary ?? ""; summary.style.display = "block";
    button.append(summary);
    button.setAttribute("aria-pressed", String(selected === item.id));
    button.addEventListener("click", () => { void select(item); }); list.append(button);
  }
  if (!list.childElementCount) list.textContent = "該当する電文はありません。";
}
document.getElementById("filter").addEventListener("change", renderList);
async function select(item) {
  const request = ++generation; selected = item.id; pages = []; page = 0; showPage(); renderList();
  reviewFrame.style.visibility = "hidden";
  const label = document.getElementById("selection"); label.textContent = "読み込み中…";
  try {
    const response = await fetch(endpoint(`/monitor/item?id=${encodeURIComponent(item.id)}`), {cache:"no-store",signal:AbortSignal.timeout(5000)});
    if(!response.ok) throw new Error("unavailable");
    const value = await response.json(); if(request !== generation) return;
    pages = value; page = 0; reviewFrame.style.visibility = pages.length ? "visible" : "hidden";
    label.textContent = `確認専用・${source(item.sourceMode)}｜${kinds[item.kind] ?? item.kind}｜${time(item.issuedAt)}発表｜${item.provider}｜${item.result}`;
    showPage();
  } catch { if(request === generation) label.textContent = "取得できません。通信状態または電文の保存件数上限を確認してください。"; }
}
async function poll() {
  try {
    const response = await fetch(endpoint("/monitor/data"), {cache:"no-store",signal:AbortSignal.timeout(5000)});
    if(!response.ok) throw new Error("disconnected");
    const data = await response.json(); latestState = data; showChannels(data); grid.classList.remove("stale");
    document.getElementById("connection").textContent = `モニター接続済み｜受信：${connectionNames[data.connection] ?? data.connection}｜確認時刻 ${time(data.serverTime)}（受信停止中の字幕は残る場合があります）`;
    telegrams = data.telegrams; const key = telegrams.map(item => item.id).join(",");
    if (key !== listKey) { listKey = key; renderList(); }
  } catch {
    grid.classList.add("stale"); document.getElementById("connection").textContent = "モニター更新停止：表示は最後に確認した内容です。CDI・OBS Local Viewの起動状態を確認してください。";
  } finally { setTimeout(poll, 1000); }
}
renderList(); void poll();
