const feedEl = document.getElementById('insightFeed');
const tabsEl = document.getElementById('assetTabs');

const assets = [...new Map(INSIGHTS.map((i) => [i.asset, i.assetName])).entries()];
let activeAsset = 'all';

function formatDate(iso) {
  const d = new Date(iso + 'T00:00:00');
  return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
}

function renderTabs() {
  if (assets.length <= 1) {
    tabsEl.style.display = 'none';
    return;
  }
  tabsEl.style.display = 'flex';
  const tabs = [['all', 'All']].concat(assets);
  tabsEl.innerHTML = tabs
    .map(
      ([symbol, label]) =>
        `<button class="asset-tab${symbol === activeAsset ? ' active' : ''}" data-asset="${symbol}">${label}</button>`
    )
    .join('');

  tabsEl.querySelectorAll('.asset-tab').forEach((tab) => {
    tab.addEventListener('click', () => {
      activeAsset = tab.dataset.asset;
      renderTabs();
      renderFeed();
    });
  });
}

function renderFeed() {
  const items = INSIGHTS.filter((i) => activeAsset === 'all' || i.asset === activeAsset).sort((a, b) =>
    b.date.localeCompare(a.date)
  );

  if (!items.length) {
    feedEl.innerHTML = '<p class="section-subtitle">No insights yet for this asset.</p>';
    return;
  }

  feedEl.innerHTML = items
    .map(
      (i) => `
    <article class="insight-card">
      <div class="insight-card__meta">
        <span class="insight-card__asset">${i.asset}</span>
        <span class="insight-card__date">${formatDate(i.date)}</span>
      </div>
      <h3>${i.title}</h3>
      <p>${i.body}</p>
    </article>
  `
    )
    .join('');
}

renderTabs();
renderFeed();
