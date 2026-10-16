(function (root) {
    'use strict';
    const maxAge = 5 * 60 * 1000;
    function validCoordinates(lat, lon, accuracy) {
        return [lat, lon, accuracy].every(v => typeof v === 'number' && Number.isFinite(v)) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180 && accuracy > 0 && accuracy <= 200;
    }
    function locationController(geo, onChange, onStatus, now = () => Date.now()) {
        let generation = 0, location = null;
        function clear(message) { location = null; onChange(null); if (message) onStatus(message); }
        return {
            request() {
                const token = ++generation;
                clear('Requesting a fresh location. Allow location access when your browser asks.');
                if (!geo) { onStatus('Location is unavailable. Use a device with location access to send feedback.'); return; }
                try {
                    geo.getCurrentPosition(position => {
                        if (token !== generation) return;
                        const c = position.coords || {};
                        if (!validCoordinates(c.latitude, c.longitude, c.accuracy)) { clear('Location must be accurate within 200 metres. Move to an open area and try again.'); return; }
                        if (!Number.isFinite(position.timestamp) || now() - position.timestamp > maxAge || position.timestamp > now() + 10000) { clear('The device returned an old location. Please try again.'); return; }
                        location = { lat: c.latitude, lon: c.longitude, accuracy: c.accuracy, captured: position.timestamp };
                        onChange(location);
                        onStatus('Location captured. Send your report within five minutes; Praja will check that you are within 1 km.');
                    }, () => {
                        if (token === generation) clear('Location unavailable or permission denied. Allow location access and try again.');
                    }, { enableHighAccuracy: true, maximumAge: 0, timeout: 20000 });
                } catch { if (token === generation) clear('Location access failed. Check browser permissions and try again.'); }
            },
            fresh() {
                if (!location) return false;
                if (now() - location.captured >= maxAge || now() < location.captured - 10000) { clear('Location expired. Choose Use my location again before sending.'); return false; }
                return true;
            }
        };
    }
    root.PrajaCommunityFeedback = { validCoordinates, locationController };
    if (!root.document) return;
    const doc = root.document, el = id => doc.getElementById(id), form = el('community-feedback-form');
    if (!form) return;
    const status = message => { el('community-location-status').textContent = message; };
    const controller = locationController(root.navigator?.geolocation, location => {
        el('community-latitude').value = location ? location.lat : '';
        el('community-longitude').value = location ? location.lon : '';
        el('community-accuracy').value = location ? location.accuracy : '';
        el('community-submit').disabled = !location;
    }, status);
    el('community-locate').addEventListener('click', () => controller.request());
    form.addEventListener('submit', event => {
        if (!controller.fresh()) { event.preventDefault(); status('Choose Use my location again before sending your report.'); }
    });
    root.setInterval(() => controller.fresh(), 1000);
})(typeof globalThis !== 'undefined' ? globalThis : this);
