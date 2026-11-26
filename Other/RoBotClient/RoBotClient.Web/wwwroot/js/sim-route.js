// Plays the map-specific replays of one route on a shared clock.
export function play(root, first, createViewer) {
    const hops = [first, ...(first.routeSegments || []).map(s => s.replay)];
    const total = hops.reduce((sum, hop) => sum + hop.duration, 0);
    let index = -1, offset = 0, active = null, stopped = false;
    const routeClock = { offset, total, timeline: [], seek };

    const start = (next, at = 0, autoplay = true) => {
        active?.stop();
        index = next;
        offset = hops.slice(0, index).reduce((sum, hop) => sum + hop.duration, 0);
        const replay = hops[index];
        routeClock.offset = offset;
        active = createViewer(replay, routeClock, completed => {
            if (stopped) return;
            routeClock.timeline.push({ index, offset, segments: completed.segments, events: completed.rp.events, vitals: completed.rp.vitals, maxHp: completed.rp.maxHp });
            if (index + 1 < hops.length) start(index + 1);
            else active.pause();
        }, () => {});
        active.setT(at);
        if (autoplay) active.play();
        return active;
    };

    function seek(time) {
        const t = Math.max(0, Math.min(total, time));
        let sum = 0, next = hops.length - 1;
        for (let i = 0; i < hops.length; i++) {
            if (t <= sum + hops[i].duration || i === hops.length - 1) { next = i; break; }
            sum += hops[i].duration;
        }
        const local = Math.max(0, t - sum);
        routeClock.timeline = routeClock.timeline.filter(part => part.index < next);
        if (next === index) { active.pause(); active.setT(local); active.selected = null; active.selectedMob = null; }
        else start(next, local, false);
        return active;
    }

    start(0);
    return { stop() { stopped = true; active?.stop(); } };
}
