// Live map page: aircraft markers plus altitude-colored, age-faded flight paths.
// All feed-derived strings are rendered with textContent, never as HTML.
window.planeWebLiveMap = (() => {
    const LAYER_KEY = 'fw.mapLayer', LABELS_KEY = 'fw.mapLabels';
    const GAP_BREAK_SEC = 300;     // don't join points more than 5 min apart
    const MAX_EXTRAPOLATE_SEC = 30;
    const PLANE_PATH = 'M12 2c.8 0 1.3.7 1.3 1.5V9l7.7 4.5v2l-7.7-2.4v5l2.2 1.6V21L12 20l-3.5 1v-1.3l2.2-1.6v-5L3 15.5v-2L10.7 9V3.5C10.7 2.7 11.2 2 12 2z';

    let map, renderer, areaLayer, trailLayer, planeLayer, labelsLayer, area, timer;
    let clockOffset = 0, windowSec = 900;
    const planes = new Map(); // hex -> { marker, el, svg, label, data, recv }
    const trails = new Map(); // hex -> { pts: [[t,lat,lon,alt]], wall, group }
    const infoCache = new Map(); // in-flight lookups only: key -> Promise<{details, route}>
    let panel = null, selected = null, selectedKey = null, autoLabels = false;

    const store = {
        get: k => { try { return localStorage.getItem(k); } catch { return null; } },
        set: (k, v) => { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
    };
    const nowSec = () => Date.now() / 1000 + clockOffset;

    // ---- colors ----
    function altColor(alt) {
        if (alt == null) return '#9e9e9e';
        if (alt <= 0) return '#a1887f';
        const f = Math.min(alt, 40000) / 40000;           // 0 ft orange → 40k+ ft magenta
        return `hsl(${Math.round(20 + f * 280)}, 100%, 55%)`;
    }
    const altBucket = alt => alt == null ? 'u' : alt <= 0 ? 'g' : Math.round(Math.min(alt, 40000) / 1000);

    // ---- map setup ----
    function baseLayers() {
        return {
            'Streets': L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors' }),
            'Dark': L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
                maxZoom: 20, subdomains: 'abcd',
                attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors &copy; <a href="https://carto.com/attributions">CARTO</a>' }),
            'Satellite': L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
                maxZoom: 19, attribution: 'Imagery &copy; Esri, Maxar, Earthstar Geographics, and the GIS User Community' }),
        };
    }
    const layerKey = name => name.toLowerCase();

    function button(text, title, onClick) {
        const C = L.Control.extend({
            onAdd() {
                const b = L.DomUtil.create('button', 'fw-map-btn');
                b.type = 'button'; b.textContent = text; b.title = title; b.setAttribute('aria-label', title);
                L.DomEvent.disableClickPropagation(b);
                L.DomEvent.on(b, 'click', onClick);
                return b;
            }
        });
        return new C({ position: 'topleft' });
    }

    function legend() {
        const C = L.Control.extend({
            onAdd() {
                const d = L.DomUtil.create('div', 'fw-legend');
                const bar = L.DomUtil.create('div', 'fw-legend-bar', d);
                bar.style.background = `linear-gradient(to right, ${[0, 10000, 20000, 30000, 40000].map(altColor).join(',')})`;
                const labels = L.DomUtil.create('div', 'fw-legend-labels', d);
                for (const t of ['GND', '10k', '20k', '30k', '40k+ ft']) L.DomUtil.create('span', '', labels).textContent = t;
                return d;
            }
        });
        return new C({ position: 'bottomleft' });
    }

    function init(el, a, defaultLayer) {
        dispose();
        renderer = L.canvas({ padding: 0.5 });
        map = L.map(el, { preferCanvas: true, worldCopyJump: true, zoomControl: true });
        map.setView([a.lat, a.lon], 9); // a view must exist before vector layers are added

        const bases = baseLayers();
        labelsLayer = L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}', { maxZoom: 19 });
        areaLayer = L.layerGroup().addTo(map);
        trailLayer = L.layerGroup().addTo(map);
        planeLayer = L.layerGroup().addTo(map);

        const saved = store.get(LAYER_KEY) || defaultLayer || 'streets';
        const startName = Object.keys(bases).find(n => layerKey(n) === saved) || 'Streets';
        bases[startName].addTo(map);
        if (startName === 'Satellite' && store.get(LABELS_KEY) !== '0') labelsLayer.addTo(map);

        L.control.layers(bases, {
            'Place labels': labelsLayer, 'Area': areaLayer, 'Flight paths': trailLayer,
        }, { position: 'topright', collapsed: false }).addTo(map);
        map.on('baselayerchange', e => {
            store.set(LAYER_KEY, layerKey(e.name));
            // Satellite is hard to read without names; follow the user's last labels choice.
            // Deferred: the layers control is still processing its inputs when this event fires.
            setTimeout(() => {
                if (!map) return;
                if (e.name === 'Satellite' && store.get(LABELS_KEY) !== '0') labelsLayer.addTo(map);
                else if (e.name !== 'Satellite' && map.hasLayer(labelsLayer)) {
                    autoLabels = true; // automatic removal must not overwrite the user's preference
                    map.removeLayer(labelsLayer);
                    autoLabels = false;
                }
            }, 0);
        });
        map.on('overlayadd overlayremove', e => {
            if (e.layer === labelsLayer && !autoLabels) store.set(LABELS_KEY, e.type === 'overlayadd' ? '1' : '0');
        });

        button('⤢', 'Fit to area', fit).addTo(map);
        button('⛶', 'Toggle fullscreen', toggleFullscreen).addTo(map);
        legend().addTo(map);
        L.control.scale({ imperial: true, metric: true }).addTo(map);

        setArea(a, true);
        timer = setInterval(animate, 250);
        panel = buildPanel(el.parentElement);
        map.on('click', () => select(null));
        document.addEventListener('keydown', onKey);
        setTimeout(() => map && map.invalidateSize(), 100);
    }

    function setArea(a, doFit) {
        if (!map) return;
        area = a;
        areaLayer.clearLayers();
        L.circleMarker([a.lat, a.lon], { radius: 5, color: '#ff3b30', fillOpacity: 1, interactive: false }).addTo(areaLayer);
        if (a.shape === 1 && a.polygon.length >= 3)
            L.polygon(a.polygon, { color: '#ffb000', weight: 2, fillOpacity: 0.05, interactive: false }).addTo(areaLayer);
        else if (a.shape === 0)
            L.circle([a.lat, a.lon], { radius: a.radiusNm * 1852, color: '#ffb000', weight: 2, fillOpacity: 0.05, interactive: false }).addTo(areaLayer);
        if (doFit) fit();
    }

    function fit() {
        if (!map || !area) return;
        let b = null;
        if (area.shape === 1 && area.polygon.length >= 3) b = L.latLngBounds(area.polygon);
        else if (area.shape === 0) b = L.latLng(area.lat, area.lon).toBounds(area.radiusNm * 1852 * 2);
        if (!b && planes.size) b = L.latLngBounds([...planes.values()].map(p => p.marker.getLatLng()));
        if (b) map.fitBounds(b, { padding: [20, 20] });
        else map.setView([area.lat, area.lon], 10);
    }

    function toggleFullscreen() {
        const doc = document, el = doc.documentElement;
        if (doc.fullscreenElement || doc.webkitFullscreenElement)
            (doc.exitFullscreen || doc.webkitExitFullscreen).call(doc);
        else (el.requestFullscreen || el.webkitRequestFullscreen || (() => {})).call(el);
    }

    // ---- data updates ----
    function update(p) {
        if (!map) return;
        clockOffset = p.now - Date.now() / 1000;
        windowSec = p.windowSec;

        for (const t of p.trails) {
            let e = trails.get(t.hex);
            if (!e) trails.set(t.hex, e = { hex: t.hex, pts: [], wall: false, group: L.layerGroup().addTo(trailLayer) });
            e.pts = t.full ? t.pts : e.pts.concat(t.pts);
            e.wall = t.wall;
        }
        const present = new Set(p.present);
        for (const [hex, e] of trails) if (!present.has(hex)) { trailLayer.removeLayer(e.group); trails.delete(hex); }

        const live = new Set();
        for (const a of p.aircraft) {
            live.add(a.hex);
            upsertPlane(a, p.asOf || nowSec());
            const t = trails.get(a.hex);
            if (t) t.wall = a.wall;
        }
        for (const [hex, pl] of planes) if (!live.has(hex)) { planeLayer.removeLayer(pl.marker); planes.delete(hex); }
        if (selected) renderLive(planes.get(selected)?.data ?? null);

        for (const e of trails.values()) drawTrail(e);
    }

    function drawTrail(e) {
        e.group.clearLayers();
        const cutoff = nowSec() - windowSec;
        const pts = e.pts.filter(p => p[0] >= cutoff);
        e.pts = pts;
        if (pts.length < 2) return;

        const sel = e.hex === selected;
        const weight = sel ? 5 : e.wall ? 3.5 : 2, dim = sel || e.wall ? 1 : 0.5;
        // Merge consecutive segments sharing a color and opacity bucket into one polyline.
        let run = null;
        const flush = () => { if (run && run.ll.length > 1)
            L.polyline(run.ll, { renderer, color: run.color, weight, opacity: run.op, interactive: false, lineCap: 'round' }).addTo(e.group); };
        for (let i = 1; i < pts.length; i++) {
            const a = pts[i - 1], b = pts[i];
            if (b[0] - a[0] > GAP_BREAK_SEC) { flush(); run = null; continue; }
            const age = (nowSec() - b[0]) / windowSec;
            const op = Math.round((0.15 + 0.85 * Math.max(0, 1 - age)) * dim * 10) / 10;
            const key = `${altBucket(b[3])}|${op}`;
            if (!run || run.key !== key) {
                flush();
                run = { key, color: altColor(b[3]), op, ll: [[a[1], a[2]]] };
            }
            run.ll.push([b[1], b[2]]);
        }
        flush();
    }

    function upsertPlane(a, asOf) {
        let pl = planes.get(a.hex);
        if (!pl) {
            const el = document.createElement('div');
            el.className = 'fw-plane';
            const svgNs = 'http://www.w3.org/2000/svg';
            const svg = document.createElementNS(svgNs, 'svg');
            svg.setAttribute('viewBox', '0 0 24 24');
            const path = document.createElementNS(svgNs, 'path');
            path.setAttribute('d', PLANE_PATH);
            svg.appendChild(path);
            const label = document.createElement('div');
            label.className = 'fw-plane-label';
            el.append(svg, label);

            const marker = L.marker([a.lat, a.lon], {
                icon: L.divIcon({ html: el, className: 'fw-plane-icon', iconSize: [28, 28], iconAnchor: [14, 14] }),
                keyboard: true, title: a.ident, alt: `Aircraft ${a.ident}`,
            }).addTo(planeLayer);
            pl = { marker, el, svg, label, data: a, recv: asOf };
            marker.on('click', ev => { L.DomEvent.stopPropagation(ev); select(a.hex); });
            marker.getElement()?.addEventListener('keydown', ev => {
                if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); ev.stopPropagation(); select(a.hex); }
            });
            planes.set(a.hex, pl);
        }
        const prev = pl.data;
        pl.data = a;
        // Restart the glide only for a new position (or when it stops moving); a repeated position
        // keeps its original timestamp so the extrapolation cap still applies.
        const moved = prev.lat !== a.lat || prev.lon !== a.lon || !a.gs || a.ground;
        if (pl.recv !== asOf && moved) {
            pl.recv = asOf;
            pl.marker.setLatLng([a.lat, a.lon]);
        }
        pl.label.textContent = a.ident;
        pl.marker.getElement()?.setAttribute('aria-label', `Aircraft ${a.ident}`);
        pl.el.classList.toggle('on-wall', !!a.wall);
        pl.el.classList.toggle('ground', !!a.ground);
        pl.svg.style.transform = `rotate(${a.trk ?? 0}deg)`;
        pl.svg.style.fill = altColor(a.alt);
        pl.el.classList.toggle('selected', a.hex === selected);
        pl.marker.setZIndexOffset(a.hex === selected ? 2000 : a.wall ? 1000 : 0);
    }

    // ---- details panel ----
    function el(tag, cls, text, parent) {
        const x = document.createElement(tag);
        if (cls) x.className = cls;
        if (text != null) x.textContent = text;
        if (parent) parent.appendChild(x);
        return x;
    }
    function link(parent, text, href) {
        const a = el('a', '', text, parent);
        a.href = href; a.target = '_blank'; a.rel = 'noopener noreferrer';
        return a;
    }
    function rows(dl, pairs) {
        dl.replaceChildren();
        for (const [k, v] of pairs) {
            if (v == null || v === '') continue;
            el('dt', '', k, dl); el('dd', '', v, dl);
        }
    }

    function buildPanel(host) {
        const p = el('aside', 'fw-panel', null, host);
        p.setAttribute('aria-live', 'polite');
        p.hidden = true;
        const close = el('button', 'fw-panel-close', '✕', p);
        close.type = 'button'; close.title = 'Close'; close.setAttribute('aria-label', 'Close details');
        close.addEventListener('click', () => select(null));
        const ref = {
            root: p,
            photo: el('div', 'fw-panel-photo', null, p),
            ident: el('div', 'fw-panel-ident', null, p),
            type: el('div', 'fw-panel-type', null, p),
            route: el('div', 'fw-panel-route', null, p),
            status: el('div', 'fw-panel-status', null, p),
        };
        el('h3', '', 'Live', p); ref.live = el('dl', 'fw-panel-grid', null, p);
        el('h3', '', 'Aircraft', p); ref.reg = el('dl', 'fw-panel-grid', null, p);
        ref.links = el('div', 'fw-panel-links', null, p);
        return ref;
    }

    function onKey(e) { if (e.key === 'Escape' && selected) select(null); }

    function select(hex) {
        const prev = selected;
        selected = hex;
        for (const pl of planes.values()) {
            pl.el.classList.toggle('selected', pl.data.hex === hex);
            pl.marker.setZIndexOffset(pl.data.hex === hex ? 2000 : pl.data.wall ? 1000 : 0);
        }
        for (const id of [prev, hex]) { const t = id && trails.get(id); if (t) drawTrail(t); }
        if (!panel) return;
        panel.root.hidden = !hex;
        if (!hex) { selectedKey = null; return; }

        const a = planes.get(hex)?.data;
        panel.root.scrollTop = 0;
        panel.ident.textContent = a?.ident ?? hex.toUpperCase();
        panel.type.textContent = a?.type ?? '';
        panel.route.replaceChildren();
        panel.photo.replaceChildren(el('div', 'fw-panel-photo-empty', 'Loading photo…'));
        rows(panel.reg, [['ICAO hex', hex.toUpperCase()], ['Registration', a?.reg]]);
        renderLinks(a, hex, null);
        renderLive(a);

        const params = new URLSearchParams();
        if (a?.reg) params.set('reg', a.reg);
        if (a?.callsign) params.set('callsign', a.callsign);
        const key = `${hex}|${params}`;
        selectedKey = key;
        if (!infoCache.has(key))
            infoCache.set(key, fetch(`/api/aircraft/${encodeURIComponent(hex)}?${params}`)
                .then(r => r.ok ? r.json() : null).catch(() => null));
        infoCache.get(key).then(info => {
            infoCache.delete(key); // only coalesces in-flight requests; HTTP/server caches control expiry
            // Ignore late responses for an earlier selection or a different request for the same aircraft.
            if (selected === hex && selectedKey === key) renderInfo(hex, a, info);
        });
    }

    function renderLive(a) {
        if (!panel || !selected) return;
        if (!a) {
            panel.status.textContent = 'Out of range — last details shown';
            panel.root.classList.add('stale');
            return;
        }
        panel.status.textContent = a.wall ? 'ON THE WALL' : '';
        panel.root.classList.remove('stale');
        rows(panel.live, [
            ['Callsign', a.callsign], ['Altitude', a.altText], ['Speed', a.spdText], ['Heading', a.hdgText],
            ['Vert. rate', a.vrText], ['Distance', a.distText], ['Squawk', a.squawk],
            ['Position', `${a.lat.toFixed(4)}, ${a.lon.toFixed(4)}`],
        ]);
    }

    function renderInfo(hex, a, info) {
        const d = info?.details;
        // Photo, with the credit and link planespotters.net requires.
        panel.photo.replaceChildren();
        if (d?.photo) {
            const img = el('img', '', null, panel.photo);
            img.src = d.photo.url; img.alt = `Photo of ${d.registration ?? hex.toUpperCase()}`;
            img.loading = 'lazy'; img.referrerPolicy = 'no-referrer';
            if (d.photo.width && d.photo.height) { img.width = d.photo.width; img.height = d.photo.height; }
            const credit = el('div', 'fw-panel-credit', null, panel.photo);
            const who = `© ${d.photo.photographer ?? 'unknown'} · ${d.photo.source}`;
            if (d.photo.link) link(credit, who, d.photo.link); else credit.textContent = who;
        } else {
            el('div', 'fw-panel-photo-empty', 'No photo available', panel.photo);
        }

        if (d) {
            const model = [d.manufacturer, d.type].filter(Boolean).join(' ');
            if (model) panel.type.textContent = model;
            rows(panel.reg, [
                ['Registration', d.registration ?? a?.reg], ['ICAO hex', hex.toUpperCase()],
                ['Type code', d.icaoType ?? a?.typeCode], ['Manufacturer', d.manufacturer], ['Model', d.type],
                ['Owner', d.owner ?? a?.op], ['Country', d.ownerCountry], ['Operator code', d.operatorFlag],
                ['Category', categoryName(a?.category)],
            ]);
        }

        const stops = info?.route?.stops;
        panel.route.replaceChildren();
        if (stops?.length >= 2) {
            stops.forEach((s, i) => {
                if (i) el('span', 'fw-panel-arrow', '→', panel.route);
                const stop = el('span', 'fw-panel-stop', null, panel.route);
                el('b', '', s.code, stop);
                el('small', '', s.city ?? s.name, stop);
            });
        }
        renderLinks(a, hex, d);
    }

    function renderLinks(a, hex, d) {
        panel.links.replaceChildren();
        link(panel.links, 'adsb.lol', `https://adsb.lol/?icao=${encodeURIComponent(hex)}`);
        const reg = d?.registration ?? a?.reg;
        if (reg) link(panel.links, 'Planespotters', `https://www.planespotters.net/search?q=${encodeURIComponent(reg)}`);
        if (a?.callsign) link(panel.links, 'FlightAware', `https://flightaware.com/live/flight/${encodeURIComponent(a.callsign)}`);
        if (reg) link(panel.links, 'FAA registry', `https://registry.faa.gov/AircraftInquiry/Search/NNumberResult?nNumberTxt=${encodeURIComponent(reg)}`)
            .hidden = !/^N[0-9A-Z]+$/i.test(reg);
    }

    function categoryName(c) {
        return ({ A1: 'Light (< 15,500 lb)', A2: 'Small (15,500–75,000 lb)', A3: 'Large (75,000–300,000 lb)',
                  A4: 'High-vortex large (B757)', A5: 'Heavy (> 300,000 lb)', A6: 'High performance', A7: 'Rotorcraft',
                  B1: 'Glider / sailplane', B2: 'Lighter-than-air', B4: 'Ultralight', B6: 'UAV / drone',
                  C1: 'Surface emergency vehicle', C2: 'Surface service vehicle' })[c] ?? c ?? null;
    }

    // Glide markers between polls using last known speed and track.
    function animate() {
        const now = nowSec();
        for (const pl of planes.values()) {
            const a = pl.data;
            if (a.ground || !a.gs || a.trk == null) continue;
            const dt = Math.min(now - pl.recv, MAX_EXTRAPOLATE_SEC);
            if (dt <= 0) continue;
            const nm = a.gs * dt / 3600, rad = a.trk * Math.PI / 180;
            const lat = a.lat + nm * Math.cos(rad) / 60;
            const lon = a.lon + nm * Math.sin(rad) / (60 * Math.cos(a.lat * Math.PI / 180));
            pl.marker.setLatLng([lat, lon]);
        }
    }

    function dispose() {
        if (timer) clearInterval(timer);
        timer = null;
        planes.clear();
        trails.clear();
        selected = null;
        document.removeEventListener('keydown', onKey);
        if (panel) { panel.root.remove(); panel = null; }
        if (map) { map.remove(); map = null; }
    }

    return { init, setArea, update, dispose };
})();
