(() => {
    const button = document.querySelector('[data-capture-location]');
    if (!button) return;
    const status = document.querySelector('[data-location-status]');
    button.addEventListener('click', () => {
        if (!navigator.geolocation) { status.textContent = 'GPS is unavailable. Enter coordinates from an authorised site record.'; return; }
        button.disabled = true;
        status.textContent = 'Requesting your current location…';
        navigator.geolocation.getCurrentPosition(position => {
            const { latitude, longitude, accuracy } = position.coords;
            if (!Number.isFinite(latitude) || !Number.isFinite(longitude) || Math.abs(latitude) > 90 || Math.abs(longitude) > 180) {
                status.textContent = 'Location was invalid. Existing coordinates were kept.';
            } else {
                document.getElementById('Latitude').value = latitude.toFixed(6);
                document.getElementById('Longitude').value = longitude.toFixed(6);
                status.textContent = `Current device location captured (estimated accuracy ${Math.round(accuracy)} metres). Confirm you are at the work site. This does not establish where an uploaded photo was taken.`;
            }
            button.disabled = false;
        }, () => {
            status.textContent = 'Location permission was denied or the device could not obtain a fix. Existing coordinates were kept. Retry onsite or enter authorised coordinates.';
            button.disabled = false;
        }, { enableHighAccuracy: true, timeout: 15000, maximumAge: 0 });
    });
})();
