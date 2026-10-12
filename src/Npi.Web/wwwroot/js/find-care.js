// Find care (docs/PRODUCT_PLAN.md step 1.8): "Use my location" fills the ZIP, and the button says what it will show.
// The page works without this script; the location is rounded to about a kilometre and only used to find the nearest ZIP.
(function () {
    'use strict';
    var form = document.querySelector('.care-form');
    if (!form) { return; }
    var zip = document.getElementById('zip');
    var submit = document.getElementById('care-submit');
    var nearMe = document.getElementById('near-me');
    var status = document.getElementById('near-me-status');
    var specialty = document.getElementById('specialty');

    function label() {
        var need = form.querySelector('input[name="need"]:checked');
        var what = 'care';
        if (need && need.dataset.plural) {
            what = need.dataset.plural.toLowerCase();
        } else if (need && need.value === 'other' && specialty.selectedIndex > 0) {
            what = specialty.options[specialty.selectedIndex].text.replace(/ \(all\)$/, '').toLowerCase();
        }
        var z = zip.value.trim();
        submit.textContent = 'Show ' + what + (/^\d{5}$/.test(z) ? ' near ' + z : ' near me');
    }

    form.addEventListener('change', label);
    zip.addEventListener('input', label);
    specialty.addEventListener('change', function () {
        if (specialty.value) { document.getElementById('need-other').checked = true; label(); }
    });

    if (navigator.geolocation) {
        nearMe.hidden = false;
        nearMe.addEventListener('click', function () {
            status.textContent = 'Finding your location…';
            navigator.geolocation.getCurrentPosition(function (pos) {
                var lat = pos.coords.latitude.toFixed(2), lon = pos.coords.longitude.toFixed(2);
                fetch('/find-care?handler=NearestZip&lat=' + lat + '&lon=' + lon)
                    .then(function (r) { return r.ok ? r.json() : { zip: null }; })
                    .then(function (d) {
                        if (d.zip) {
                            zip.value = d.zip;
                            status.textContent = 'Using ZIP ' + d.zip + ', the nearest to you.';
                            label();
                        } else {
                            status.textContent = 'We could not match your location to a ZIP code. Please type it.';
                        }
                    })
                    .catch(function () { status.textContent = 'We could not look up your location. Please type your ZIP code.'; });
            }, function () {
                status.textContent = 'Your browser did not share your location. Please type your ZIP code.';
            }, { maximumAge: 600000, timeout: 15000 });
        });
    }
})();
