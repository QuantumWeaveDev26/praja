(() => {
    const button = document.getElementById('citizen-location-capture');
    if (!button) return;
    const clear = document.getElementById('citizen-location-clear');
    const latitude = document.getElementById('citizen-latitude');
    const longitude = document.getElementById('citizen-longitude');
    const accuracy = document.getElementById('citizen-location-accuracy');
    const status = document.getElementById('citizen-location-status');
    const coordinates = document.getElementById('citizen-location-coordinates');
    let request = 0;
    const edited = () => {
        ++request;
        button.disabled = false;
        accuracy.value = '';
        status.textContent = 'Coordinates edited. Device accuracy cleared. Check the incident location before submitting.';
    };
    latitude.addEventListener('input', edited);
    longitude.addEventListener('input', edited);
    clear.addEventListener('click', () => {
        ++request;
        button.disabled = false;
        latitude.value = longitude.value = accuracy.value = '';
        status.textContent = 'Coordinates cleared. You can submit using the district and locality below.';
    });
    button.addEventListener('click', () => {
        if (!navigator.geolocation) {
            status.textContent = 'Device location is unavailable. Enter the district and locality; coordinates are optional.';
            return;
        }
        const current = ++request;
        button.disabled = true;
        status.textContent = 'Requesting location permission and finding this device’s position…';
        const failed = error => {
            if (current !== request) return;
            button.disabled = false;
            const reason = error?.code === 1 ? 'Location permission denied.' : error?.code === 3 ? 'Location request timed out.' : 'Device could not obtain a location.';
            status.textContent = reason + ' Existing coordinates were kept. Retry at the incident or submit using the district and locality.';
        };
        try {
            navigator.geolocation.getCurrentPosition(position => {
                if (current !== request) return;
                button.disabled = false;
                const fix = position.coords;
                if (!fix || !Number.isFinite(fix.latitude) || !Number.isFinite(fix.longitude) || Math.abs(fix.latitude) > 90 || Math.abs(fix.longitude) > 180 || !Number.isFinite(fix.accuracy) || fix.accuracy < 0 || fix.accuracy > 100000) {
                    status.textContent = 'Device returned an invalid location. Existing coordinates were kept. Coordinates are optional.';
                    return;
                }
                latitude.value = fix.latitude.toFixed(6);
                longitude.value = fix.longitude.toFixed(6);
                accuracy.value = String(fix.accuracy);
                coordinates.open = true;
                status.textContent = `Current device location captured (estimated accuracy ${Math.round(fix.accuracy)} metres). Confirm you are at the incident. Enter the district and locality below.`;
            }, failed, { enableHighAccuracy: true, timeout: 15000, maximumAge: 0 });
        } catch { failed(); }
    });
})();
