// Leaflet interop for the settings page.
window.planeWebMap = (() => {
    let map, drawn, circle, centerMarker, dotnet, aircraftLayer;

    function emitPolygon(layer) {
        const pts = layer.getLatLngs()[0].map(p => [+p.lat.toFixed(5), +p.lng.toFixed(5)]);
        dotnet.invokeMethodAsync('OnPolygonDrawn', JSON.stringify(pts));
    }

    function init(el, ref, s) {
        dotnet = ref;
        if (map) { map.remove(); }
        map = L.map(el).setView([s.lat, s.lon], 9);
        // Standard OSM tiles (no API key); darkened via CSS (.fw-dark-tiles).
        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19, className: 'fw-dark-tiles',
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        }).addTo(map);

        drawn = new L.FeatureGroup().addTo(map);
        aircraftLayer = L.layerGroup().addTo(map);
        map.addControl(new L.Control.Draw({
            draw: {
                polygon: { allowIntersection: false, shapeOptions: { color: '#ffb000' } },
                rectangle: { shapeOptions: { color: '#ffb000' } },
                polyline: false, circle: false, marker: false, circlemarker: false
            },
            edit: { featureGroup: drawn }
        }));

        map.on(L.Draw.Event.CREATED, e => { drawn.clearLayers(); drawn.addLayer(e.layer); emitPolygon(e.layer); });
        map.on(L.Draw.Event.EDITED, e => e.layers.eachLayer(emitPolygon));
        map.on(L.Draw.Event.DELETED, () => dotnet.invokeMethodAsync('OnPolygonDrawn', '[]'));
        map.on('click', e => {
            if (map._fwDrawing) return;
            dotnet.invokeMethodAsync('OnCenterPicked', +e.latlng.lat.toFixed(5), +e.latlng.lng.toFixed(5));
        });
        map.on(L.Draw.Event.DRAWSTART, () => map._fwDrawing = true);
        map.on(L.Draw.Event.DRAWSTOP, () => setTimeout(() => map._fwDrawing = false, 50));

        update(s, true);
        setTimeout(() => map.invalidateSize(), 100);
    }

    function update(s, fit) {
        if (!map) return;
        if (circle) map.removeLayer(circle);
        if (centerMarker) map.removeLayer(centerMarker);
        centerMarker = L.circleMarker([s.lat, s.lon], { radius: 5, color: '#ff3b30', fillOpacity: 1 }).addTo(map);
        drawn.clearLayers();
        if (s.shape === 1 && s.polygon.length >= 3) {
            const poly = L.polygon(s.polygon, { color: '#ffb000' });
            drawn.addLayer(poly);
            if (fit) map.fitBounds(poly.getBounds(), { padding: [20, 20] });
        } else {
            circle = L.circle([s.lat, s.lon], { radius: s.radiusNm * 1852, color: '#ffb000', fillOpacity: 0.08 }).addTo(map);
            if (fit) map.fitBounds(circle.getBounds(), { padding: [20, 20] });
        }
    }

    // Recenter on a picked location (works for radius and polygon modes; keeps the current zoom unless the
    // radius circle would not fit, in which case it fits the circle).
    function centerOn(lat, lon, radiusNm) {
        if (!map) return;
        if (radiusNm > 0) {
            map.fitBounds(L.circle([lat, lon], { radius: radiusNm * 1852 }).getBounds(), { padding: [20, 20] });
        } else {
            map.setView([lat, lon]);
        }
    }

    function setAircraft(list) {
        if (!aircraftLayer) return;
        aircraftLayer.clearLayers();
        for (const a of list) {
            // Feed values are untrusted: render as text, never HTML.
            const label = document.createElement('span');
            label.textContent = a.label;
            L.circleMarker([a.lat, a.lon], { radius: 4, color: '#39ff14', fillOpacity: 0.9, weight: 1 })
                .bindTooltip(label).addTo(aircraftLayer);
        }
    }

    function locate() {
        return new Promise(resolve => {
            if (!navigator.geolocation) return resolve(null);
            navigator.geolocation.getCurrentPosition(
                p => resolve([+p.coords.latitude.toFixed(5), +p.coords.longitude.toFixed(5)]),
                () => resolve(null), { timeout: 8000 });
        });
    }

    function dispose() { if (map) { map.remove(); map = null; } }

    function requestFullscreen() {
        const el = document.documentElement;
        (el.requestFullscreen || el.webkitRequestFullscreen || (() => {})).call(el);
    }

    return { init, update, centerOn, setAircraft, locate, dispose, requestFullscreen };
})();

// Utilities for combobox/dropdown accessibility
window.planeWebUI = {
    scrollIntoView(elementId) {
        const el = document.getElementById(elementId);
        if (el) el.scrollIntoView({ block: 'nearest' });
    }
};
