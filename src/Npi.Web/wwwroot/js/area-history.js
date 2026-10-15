// An area's figures month by month (docs/PRODUCT_PLAN.md step 3.3): a D3 column chart of one series at a time, from the
// JSON the page embeds (#area-history-data). The table under it ("Every month as a table") is the text version, so the
// chart itself is hidden from screen readers and left out without D3.
(function () {
    'use strict';
    var root = document.getElementById('area-history-chart');
    var source = document.getElementById('area-history-data');
    if (!root || !source || !window.d3) {
        return;
    }

    var d3 = window.d3;
    var data = JSON.parse(source.textContent);
    var tabs = root.querySelector('.tabs');
    var canvas = root.querySelector('.history-canvas');
    var note = root.querySelector('.history-note');
    var css = getComputedStyle(document.documentElement);
    var accent = (css.getPropertyValue('--accent') || '#0E6A73').trim();
    var error = (css.getPropertyValue('--error') || '#A3361F').trim();
    var warn = (css.getPropertyValue('--warn') || '#7A4E00').trim();
    var muted = (css.getPropertyValue('--muted') || '#4E5F58').trim();
    // Drawn at the canvas's own width (redrawn when it changes), so the labels keep their size on a phone.
    var width = 760;
    var height = 260;
    var shown = 'newProviders';
    var margin = { top: 12, right: 8, bottom: 28, left: 52 };
    var names = {
        newProviders: 'New NPIs', deactivated: 'Deactivated', movedIn: 'Moved in', movedOut: 'Moved out',
        netChange: 'Net change', providers: 'Providers', quality: 'Record quality',
    };

    function label(key) {
        var parts = key.split('-');
        return new Date(Date.UTC(+parts[0], +parts[1] - 1, 1)).toLocaleString('en-US', { month: 'short', year: 'numeric', timeZone: 'UTC' });
    }

    function value(m, series) {
        return series === 'quality'
            ? (m.qualityCurrent === null || m.qualityCurrent === undefined ? null : m.qualityCurrent + m.qualityCheck + m.qualityStale)
            : m[series];
    }

    function draw(series) {
        shown = series;
        width = Math.max(300, Math.round(canvas.getBoundingClientRect().width) || 760);
        height = width < 500 ? 220 : 260;
        // The months the series is known for, from its first one on.
        var all = data.months;
        var start = all.findIndex(function (m) { return value(m, series) !== null && value(m, series) !== undefined; });
        var months = start < 0 ? [] : all.slice(start);
        canvas.textContent = '';
        if (months.length === 0) {
            note.textContent = names[series] + ': not counted yet' + (data.trackedFrom ? ' (recorded from ' + label(data.trackedFrom) + ').' : '.');
            return;
        }

        var x = d3.scaleBand().domain(months.map(function (m) { return m.month; })).range([margin.left, width - margin.right]).padding(0.18);
        var svg = d3.select(canvas).append('svg').attr('viewBox', [0, 0, width, height].join(' '));
        // Years under a long series (each January), else every month or every few, as many as fit.
        var every = Math.max(1, Math.ceil(months.length / (width < 500 ? 4 : 10)));
        var ticks = months.filter(function (m, i) { return months.length > 24 ? m.month.slice(5) === '01' : i % every === 0; })
            .map(function (m) { return m.month; });

        if (series === 'quality') {
            var y = d3.scaleLinear().domain([0, 100]).range([height - margin.bottom, margin.top]);
            var parts = [['qualityCurrent', accent, 'current'], ['qualityCheck', warn, 'check'], ['qualityStale', error, 'stale']];
            var columns = svg.append('g').selectAll('g').data(months.filter(function (m) { return value(m, series) > 0; })).join('g');
            parts.forEach(function (part, i) {
                columns.append('rect')
                    .attr('x', function (m) { return x(m.month); })
                    .attr('width', x.bandwidth())
                    .attr('y', function (m) {
                        var total = value(m, series);
                        var below = parts.slice(i + 1).reduce(function (s, p) { return s + m[p[0]]; }, 0);
                        return y((below + m[part[0]]) / total * 100);
                    })
                    .attr('height', function (m) { return y(0) - y(m[part[0]] / value(m, series) * 100); })
                    .attr('fill', part[1])
                    .append('title').text(function (m) { return label(m.month) + ': ' + (m[part[0]] / value(m, series) * 100).toFixed(0) + '% ' + part[2]; });
            });
            svg.append('g').attr('transform', 'translate(' + margin.left + ',0)').call(d3.axisLeft(y).ticks(4).tickFormat(function (v) { return v + '%'; }));
            note.textContent = 'Record quality at each month\'s last count: current (teal), check (amber), stale (red).';
        } else {
            var values = months.map(function (m) { return value(m, series) || 0; });
            var y2 = d3.scaleLinear().domain([Math.min(0, d3.min(values)), Math.max(1, d3.max(values))]).nice().range([height - margin.bottom, margin.top]);
            svg.append('g').selectAll('rect').data(months).join('rect')
                .attr('x', function (m) { return x(m.month); })
                .attr('width', x.bandwidth())
                .attr('y', function (m) { return y2(Math.max(0, value(m, series) || 0)); })
                .attr('height', function (m) { return Math.abs(y2(value(m, series) || 0) - y2(0)); })
                .attr('fill', function (m) { return (value(m, series) || 0) < 0 ? error : accent; })
                .attr('fill-opacity', function (m) { return m.partial ? 0.45 : m.basis === 'derived' ? 0.7 : m.basis === 'archive' ? 0.85 : 1; })
                .append('title').text(function (m) {
                    var v = value(m, series);
                    return label(m.month) + (m.partial ? ' (so far)' : '') + ': ' + (v === null || v === undefined ? 'not counted' : v.toLocaleString('en-US'));
                });
            svg.append('g').attr('transform', 'translate(' + margin.left + ',0)').call(d3.axisLeft(y2).ticks(4, 's'));
            svg.append('line').attr('x1', margin.left).attr('x2', width - margin.right).attr('y1', y2(0)).attr('y2', y2(0)).attr('stroke', muted);
            var first = months[0].month;
            var archived = data.archiveFrom && data.archiveTo && first <= data.archiveTo;
            note.textContent = names[series] + ' from ' + label(first) + '.'
                + (archived ? ' To ' + label(data.archiveTo) + ': from the archived monthly NPPES files (CMS NPPES; historical copies via NBER), by primary practice location.' : '')
                + (data.trackedFrom && first < data.trackedFrom && !archived ? ' Paler columns (before ' + label(data.trackedFrom) + ') are worked out from today\'s data.' : '')
                + (months[months.length - 1].partial ? ' The last column is the month so far.' : '');
        }

        svg.append('g').attr('transform', 'translate(0,' + (height - margin.bottom) + ')')
            .call(d3.axisBottom(x).tickValues(ticks).tickFormat(function (key) { return months.length > 24 ? key.slice(0, 4) : label(key); }));
    }

    tabs.hidden = false;
    tabs.addEventListener('click', function (event) {
        var button = event.target.closest('button[data-series]');
        if (!button) {
            return;
        }

        tabs.querySelectorAll('button').forEach(function (b) { b.setAttribute('aria-pressed', b === button ? 'true' : 'false'); });
        draw(button.dataset.series);
    });
    draw('newProviders');
    var last = width;
    window.addEventListener('resize', function () {
        if (Math.abs(Math.round(canvas.getBoundingClientRect().width) - last) > 40) {
            draw(shown);
            last = width;
        }
    });
})();
