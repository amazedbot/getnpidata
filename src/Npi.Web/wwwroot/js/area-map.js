// Area map (docs/PRODUCT_PLAN.md step 3.3): the states, or the counties (of one state or nationwide), coloured by a statistic
// from /area/map.json, drawn with D3 from the Census shapes in /maps (already projected: AlbersUSA with Alaska, Hawaii and
// Puerto Rico as insets). Hover or focus an area for its numbers; a click or Enter opens its page. The page's tables carry
// the same numbers without JavaScript, so the drawing is left out when D3 or topojson-client didn't load.
(function () {
    'use strict';
    var root = document.getElementById('area-choropleth');
    if (!root || !window.d3 || !window.topojson || !window.fetch) {
        return;
    }

    var d3 = window.d3;
    var cfg = root.dataset;
    var form = root.querySelector('.choropleth-controls');
    var canvas = root.querySelector('.choropleth-canvas');
    var tip = root.querySelector('.choropleth-tip');
    var legend = root.querySelector('.choropleth-legend');
    var summary = root.querySelector('.choropleth-summary');
    var width = 975;
    var height = 610;
    var shapes = null;
    var cache = new Map();
    var css = getComputedStyle(document.documentElement);
    var accent = (css.getPropertyValue('--accent') || '#0E6A73').trim();
    var error = (css.getPropertyValue('--error') || '#A3361F').trim();
    var nodata = '#E4E9E6';
    // Light to dark teal (the site's accent), and for net change red through neutral to teal.
    var sequential = d3.quantize(d3.interpolateRgb('#E3F1EF', accent), 5);
    var diverging = [error, '#D68C73', '#F1D9CF', '#EEF2F0', '#B8DCD8', '#5FA6A8', accent];
    var shortageNames = { PC: 'primary care', DH: 'dental', MH: 'mental health' };
    var many = 300; // more areas than this aren't tab stops (the national county map); the tables list them

    form.hidden = false;

    function level() {
        var checked = form.querySelector('input[name="level"]:checked');
        return checked ? checked.value : cfg.level;
    }

    function selected(name) {
        var field = form.elements.namedItem(name);
        return field ? field.value : '';
    }

    function monthly() {
        var option = form.elements.namedItem('stat').selectedOptions[0];
        return option && option.dataset.monthly === '1';
    }

    function url() {
        var params = new URLSearchParams();
        params.set('level', level());
        if (level() === 'county' && cfg.state) {
            params.set('state', cfg.state);
        }

        params.set('stat', selected('stat'));
        if (monthly() && selected('month')) {
            params.set('month', selected('month'));
        }

        if (cfg.category) {
            params.set('category', cfg.category);
        }

        if (cfg.subcategory) {
            params.set('subcategory', cfg.subcategory);
        }

        return cfg.values + '?' + params.toString();
    }

    function load(address) {
        if (!cache.has(address)) {
            cache.set(address, fetch(address, { headers: { Accept: 'application/json' } }).then(function (r) {
                if (!r.ok) {
                    throw new Error(r.status);
                }

                return r.json();
            }));
        }

        return cache.get(address);
    }

    function format(stat, value) {
        if (value === null || value === undefined) {
            return 'no data';
        }

        if (stat === 'stale') {
            return value.toFixed(0) + '%';
        }

        if (stat === 'per100k' || stat === 'newPer100k') {
            return value >= 10 ? Math.round(value).toLocaleString('en-US') : value.toFixed(1);
        }

        var text = Math.round(value).toLocaleString('en-US');
        return stat === 'net' && value > 0 ? '+' + text : stat === 'net' && value < 0 ? '−' + text.replace('-', '') : text;
    }

    // Fifths of the areas with data (quantiles), or for net change bands either side of zero.
    function scale(stat, values) {
        var known = values.filter(function (v) { return v !== null && v !== undefined; });
        if (known.length === 0) {
            return null;
        }

        if (stat === 'net') {
            var abs = known.map(Math.abs).sort(d3.ascending);
            var mid = Math.max(1, d3.quantile(abs, 0.5));
            var top = Math.max(mid + 1, d3.quantile(abs, 0.9));
            return d3.scaleThreshold().domain([-top, -mid, -0.5, 0.5, mid, top]).range(diverging);
        }

        var distinct = Array.from(new Set(known)).length;
        return d3.scaleQuantile().domain(known).range(sequential.slice(0, Math.max(1, Math.min(5, distinct))));
    }

    function bands(stat, colour) {
        if (stat === 'net') {
            var t = colour.domain();
            return [
                [colour.range()[0], 'below ' + format(stat, t[0])],
                [colour.range()[1], format(stat, t[0]) + ' to ' + format(stat, t[1])],
                [colour.range()[2], 'a small fall'],
                [colour.range()[3], 'no change'],
                [colour.range()[4], 'a small rise'],
                [colour.range()[5], format(stat, t[4]) + ' to ' + format(stat, t[5])],
                [colour.range()[6], 'above ' + format(stat, t[5])],
            ];
        }

        var cuts = colour.quantiles();
        var domain = colour.domain();
        var low = d3.min(domain);
        var high = d3.max(domain);
        return colour.range().map(function (c, i) {
            var from = i === 0 ? low : cuts[i - 1];
            var to = i === cuts.length ? high : cuts[i];
            return [c, format(stat, from) + (to > from ? '–' + format(stat, to) : '')];
        });
    }

    function linkFor(area, lvl) {
        var path = lvl === 'state' ? '/area/' + area.id : '/area/' + area.state + '/' + area.id;
        var params = new URLSearchParams();
        if (cfg.category) {
            params.set('category', cfg.category);
        }

        if (cfg.subcategory) {
            params.set('subcategory', cfg.subcategory);
        }

        var q = params.toString();
        return q ? path + '?' + q : path;
    }

    function describe(area, data, lvl) {
        var name = lvl === 'county' ? area.name + ', ' + area.state : area.name;
        return name + ': ' + format(data.stat, area.value) + ' (' + data.statLabel.toLowerCase() + ')';
    }

    function showTip(area, data, lvl, x, y) {
        tip.textContent = '';
        var title = document.createElement('strong');
        title.textContent = lvl === 'county' ? area.name + ', ' + area.state : area.name;
        tip.appendChild(title);
        var lines = [data.statLabel + ': ' + format(data.stat, area.value)];
        if (area.population) {
            lines.push(area.population.toLocaleString('en-US') + ' people');
        }

        if (area.shortages && area.shortages.length) {
            lines.push('Shortage areas: ' + area.shortages.map(function (s) { return shortageNames[s] || s; }).join(', '));
        }

        lines.forEach(function (text) {
            var line = document.createElement('span');
            line.textContent = text;
            tip.appendChild(line);
        });
        tip.hidden = false;
        var box = root.getBoundingClientRect();
        var left = Math.min(Math.max(0, x - box.left + 12), box.width - tip.offsetWidth - 4);
        tip.style.left = left + 'px';
        tip.style.top = (y - box.top + 12) + 'px';
    }

    function draw(data) {
        var lvl = data.level;
        var object = lvl === 'state' ? shapes.objects.states : shapes.objects.counties;
        var byId = new Map(data.values.map(function (v) { return [v.id, v]; }));
        var features = window.topojson.feature(shapes, object).features.filter(function (f) { return byId.has(f.id); });
        var outline = { type: 'FeatureCollection', features: features };
        var projection = d3.geoIdentity().reflectY(true).fitExtent([[4, 4], [width - 4, height - 4]], outline);
        var path = d3.geoPath(projection);
        var colour = scale(data.stat, data.values.map(function (v) { return v.value; }));
        var shortage = lvl === 'county' ? selected('shortage') : '';
        var focusable = features.length <= many;

        canvas.textContent = '';
        var svg = d3.select(canvas).append('svg')
            .attr('viewBox', [0, 0, width, height].join(' '))
            .attr('role', 'group')
            .attr('aria-label', 'Map: ' + data.statLabel.toLowerCase() + (data.month ? ', ' + data.month.slice(0, 7) : ''));
        var hatch = svg.append('defs').append('pattern').attr('id', 'shortage-hatch').attr('patternUnits', 'userSpaceOnUse')
            .attr('width', 6).attr('height', 6).attr('patternTransform', 'rotate(45)');
        hatch.append('rect').attr('width', 2).attr('height', 6).attr('fill', '#17231F').attr('fill-opacity', 0.55);

        var areas = svg.append('g').attr('class', 'areas').selectAll('path').data(features).join('path')
            .attr('d', path)
            .attr('fill', function (f) {
                var v = byId.get(f.id).value;
                return v === null || v === undefined || !colour ? nodata : colour(v);
            })
            .attr('class', function (f) { return f.id === cfg.county ? 'area current' : 'area'; })
            .attr('tabindex', focusable ? 0 : null)
            .attr('role', focusable ? 'link' : null)
            .attr('aria-label', function (f) { return focusable ? describe(byId.get(f.id), data, lvl) : null; });

        if (shortage) {
            svg.append('g').attr('class', 'shortage').attr('aria-hidden', 'true').selectAll('path')
                .data(features.filter(function (f) { return (byId.get(f.id).shortages || []).indexOf(shortage) >= 0; }))
                .join('path').attr('d', path).attr('fill', 'url(#shortage-hatch)');
        }

        // State lines over the counties; the current county on top.
        if (lvl === 'county') {
            var states = window.topojson.feature(shapes, shapes.objects.states).features
                .filter(function (f) { return !cfg.state || f.id === cfg.state; });
            svg.append('g').attr('class', 'state-lines').attr('aria-hidden', 'true').selectAll('path').data(states).join('path').attr('d', path);
        }

        var current = features.filter(function (f) { return f.id === cfg.county || (lvl === 'state' && f.id === cfg.state); });
        svg.append('g').attr('class', 'current-line').attr('aria-hidden', 'true').selectAll('path').data(current).join('path').attr('d', path);

        areas.on('mousemove', function (event, f) { showTip(byId.get(f.id), data, lvl, event.clientX, event.clientY); })
            .on('mouseleave blur', function () { tip.hidden = true; })
            .on('focus', function (event, f) {
                var r = event.target.getBoundingClientRect();
                showTip(byId.get(f.id), data, lvl, r.left + r.width / 2, r.top + r.height / 2);
            })
            .on('click', function (event, f) { window.location.href = linkFor(byId.get(f.id), lvl); })
            .on('keydown', function (event, f) {
                if (event.key === 'Enter') {
                    window.location.href = linkFor(byId.get(f.id), lvl);
                }
            });

        legend.textContent = '';
        var list = document.createElement('ul');
        list.setAttribute('aria-label', 'Legend: ' + data.statLabel);
        (colour ? bands(data.stat, colour) : []).concat([[nodata, 'no data']]).forEach(function (band) {
            var item = document.createElement('li');
            var swatch = document.createElement('span');
            swatch.className = 'swatch';
            swatch.style.background = band[0];
            item.appendChild(swatch);
            item.appendChild(document.createTextNode(band[1]));
            list.appendChild(item);
        });
        if (shortage) {
            var item = document.createElement('li');
            var swatch = document.createElement('span');
            swatch.className = 'swatch hatched';
            item.appendChild(swatch);
            item.appendChild(document.createTextNode(shortageNames[shortage] + ' shortage area'));
            list.appendChild(item);
        }

        legend.appendChild(list);

        var ranked = data.values.filter(function (v) { return v.value !== null && v.value !== undefined; })
            .sort(function (x, y) { return y.value - x.value; });
        summary.textContent = ranked.length === 0
            ? 'No data for this ' + (monthly() ? 'month' : 'specialty') + ' yet.'
            : 'Highest: ' + describe(ranked[0], data, lvl).replace(/ \(.*\)$/, '') + '. Lowest: '
                + describe(ranked[ranked.length - 1], data, lvl).replace(/ \(.*\)$/, '') + '.'
                + (focusable ? '' : ' Pick a state for its counties one by one.');
    }

    function refresh() {
        form.querySelector('.month-control').hidden = !monthly();
        form.querySelector('.shortage-control').hidden = level() !== 'county';
        summary.textContent = 'Loading the map…';
        var shapesReady = shapes ? Promise.resolve(shapes) : fetch(cfg.shapes).then(function (r) { return r.json(); });
        Promise.all([shapesReady, load(url())]).then(function (results) {
            shapes = results[0];
            draw(results[1]);
        }).catch(function () {
            summary.textContent = 'The map could not be loaded; the tables below have the same numbers.';
        });
    }

    form.addEventListener('change', refresh);
    form.addEventListener('submit', function (event) { event.preventDefault(); });
    refresh();
})();
