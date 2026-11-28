// FramePacer (src/frame_pacer.h) on a fake clock: the interval, the origin event with an offset, late waits, timeouts,
// the latency limit, tokens and their validation, statistics, and suspend/resume with re-registration. No GPU.
#include "../src/frame_pacer.h"

#include <chrono>
#include <cstdio>
#include <future>
#include <limits>
#include <thread>

using namespace leonida;

#define EXPECT(condition) do { \
    if (!(condition)) { std::fprintf(stderr, "line %d: %s\n", __LINE__, #condition); return 1; } \
} while (0)

namespace {

constexpr std::uint32_t period = 16000;  // us; round numbers keep the arithmetic readable

struct Result {
    std::int32_t hr;
    std::uint64_t token;
};

// Runs a wait on its own thread.
std::future<Result> wait_async(FramePacer& pacer, std::uint32_t timeout_ms) {
    return std::async(std::launch::async, [&pacer, timeout_ms] {
        Result r{0, 0};
        r.hr = pacer.wait_frame_event(frame_event_origin, timeout_ms, 0, &r.token);
        return r;
    });
}

// True when the wait is still blocked after real time has had a chance to let it through.
bool blocked(std::future<Result>& f) {
    return f.wait_for(std::chrono::milliseconds(40)) == std::future_status::timeout;
}

bool setup(FramePacer& pacer, std::uint32_t latency, std::uint32_t offset = 0) {
    return pacer.set_frame_interval(period, latency, 0) == pacer_hr::ok &&
           pacer.schedule_frame_event(frame_event_origin, offset, 0) == pacer_hr::ok;
}

}  // namespace

int main() {
    // Arguments and state errors.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        std::uint64_t token = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::invalid_state);  // nothing set
        EXPECT(pacer.schedule_frame_event(frame_event_origin, 0, 0) == pacer_hr::invalid_state);      // no interval
        EXPECT(pacer.set_frame_interval(0, 1, 0) == pacer_hr::invalid_arg);
        EXPECT(pacer.set_frame_interval(period, 1, 0) == pacer_hr::ok);
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::invalid_state);  // not scheduled
        EXPECT(pacer.schedule_frame_event(7, 0, 0) == pacer_hr::invalid_arg);                         // unknown event
        EXPECT(pacer.schedule_frame_event(frame_event_origin, period, 0) == pacer_hr::invalid_arg);   // offset >= period
        EXPECT(pacer.schedule_frame_event(frame_event_origin, 0, 0) == pacer_hr::ok);
        EXPECT(pacer.wait_frame_event(3, 0, 0, &token) == pacer_hr::invalid_arg);
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, nullptr) == pacer_hr::invalid_arg);
        EXPECT(pacer.registered());
        // Setting the interval again keeps the scheduled event armed (it is re-based, not dropped).
        EXPECT(pacer.set_frame_interval(period, 1, 0) == pacer_hr::ok);
        EXPECT(pacer.registered());
    }

    // The origin event: each wait returns at the next vblank, with the next token.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1));
        auto first = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(first));
        clock.advance_us(period - 1);
        EXPECT(blocked(first));
        clock.advance_us(1);
        Result r = first.get();
        EXPECT(r.hr == pacer_hr::ok && r.token == 1);
        auto second = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(second));
        clock.advance_us(period);
        r = second.get();
        EXPECT(r.hr == pacer_hr::ok && r.token == 2);
        EXPECT(pacer.tokens_issued() == 2);
    }

    // An offset delays the event inside the period: origin n comes at n * period + offset.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1, 5000));
        auto wait = wait_async(pacer, infinite_timeout_ms);
        clock.advance_us(period);
        EXPECT(blocked(wait));
        clock.advance_us(4999);
        EXPECT(blocked(wait));
        clock.advance_us(1);
        EXPECT(wait.get().hr == pacer_hr::ok);
        std::uint32_t count = 1;
        FrameStatistic stat;
        EXPECT(pacer.get_frame_statistics(1, stat_frame_event, &count, &stat) == pacer_hr::ok);
        EXPECT(count == 1 && stat.type == stat_frame_event && stat.time_us == period + 5000);
    }

    // Scheduling later than the interval was set makes the next vblank the first origin, not the missed ones.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(pacer.set_frame_interval(period, 1, 0) == pacer_hr::ok);
        clock.advance_us(period * 5 + 100);
        EXPECT(pacer.schedule_frame_event(frame_event_origin, 0, 0) == pacer_hr::ok);
        auto wait = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(wait));
        clock.advance_us(period - 100);
        EXPECT(wait.get().token == 1);
    }

    // A late wait returns at once with the newest passed origin; the missed ones are skipped. Tokens still count
    // one per wait.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1));
        clock.advance_us(period * 3 + period / 2);
        std::uint64_t token = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok && token == 1);
        std::uint32_t count = 1;
        FrameStatistic stat;
        EXPECT(pacer.get_frame_statistics(1, stat_frame_event, &count, &stat) == pacer_hr::ok);
        EXPECT(stat.time_us == period * 3);  // origin 3, not 1
        auto next = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(next));
        clock.advance_us(period / 2);
        EXPECT(next.get().token == 2);
    }

    // Timeouts: 0 polls, a finite one counts fake time, the token is left alone, and a later wait still works.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1));
        std::uint64_t token = 99;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::timeout && token == 99);
        auto wait = wait_async(pacer, 5);  // 5 ms = 5000 us of fake time
        EXPECT(blocked(wait));
        clock.advance_us(4999);
        EXPECT(blocked(wait));
        clock.advance_us(1);
        Result r = wait.get();
        EXPECT(r.hr == pacer_hr::timeout && r.token == 0);
        EXPECT(pacer.tokens_issued() == 0);
        auto again = wait_async(pacer, 100);
        EXPECT(blocked(again));
        clock.advance_us(period - 5000);  // the vblank, 11 ms after the first wait gave up
        r = again.get();
        EXPECT(r.hr == pacer_hr::ok && r.token == 1);
        // Poll with the origin due.
        clock.advance_us(period);
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok && token == 2);
    }

    // Latency 1: a frame presented late is still in flight at the next origin, so the wait holds until it is shown.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1));
        clock.advance_us(period);
        std::uint64_t token = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok && token == 1);
        clock.advance_us(period + period / 2);  // frame 1 is presented 1.5 vblanks late: now = 2.5 periods
        EXPECT(pacer.check_present_token(1) == pacer_hr::ok);
        pacer.commit_present_token(1, 1);
        EXPECT(pacer.frames_in_flight() == 1);
        auto wait = wait_async(pacer, infinite_timeout_ms);  // origin 2 has passed, but one frame is pending
        EXPECT(blocked(wait));
        clock.advance_us(period / 2 - 1);
        EXPECT(blocked(wait));
        clock.advance_us(1);  // vblank 3: frame 1 shows, the wait goes through with the origin of that vblank
        EXPECT(wait.get().token == 2);
        EXPECT(pacer.frames_in_flight() == 0);
        std::uint32_t count = 2;
        FrameStatistic stats[2];
        EXPECT(pacer.get_frame_statistics(1, stat_present | stat_display, &count, stats) == pacer_hr::ok);
        EXPECT(count == 2 && stats[0].type == stat_present && stats[0].time_us == period * 5 / 2 &&
               stats[1].type == stat_display && stats[1].time_us == period * 3);
    }

    // Latency 2 lets two frames queue; a third wait is held until the oldest has been shown.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 2));
        std::uint64_t tokens[3] = {};
        for (int i = 0; i < 2; ++i) {
            clock.advance_us(period);
            EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &tokens[i]) == pacer_hr::ok);
        }
        // Now = 2 periods; the app is slow and presents both frames at 3.5 periods: they show at vblanks 4 and 5.
        clock.advance_us(period * 3 / 2);
        EXPECT(pacer.check_present_token(tokens[0]) == pacer_hr::ok);
        pacer.commit_present_token(tokens[0], 1);
        EXPECT(pacer.check_present_token(tokens[1]) == pacer_hr::ok);
        pacer.commit_present_token(tokens[1], 1);
        EXPECT(pacer.frames_in_flight() == 2);
        auto wait = wait_async(pacer, infinite_timeout_ms);  // origin 3 has passed, but both slots are taken
        EXPECT(blocked(wait));
        clock.advance_us(period / 2);  // vblank 4 shows frame 1: one in flight, the wait goes through
        Result r = wait.get();
        EXPECT(r.hr == pacer_hr::ok && r.token == 3);
        EXPECT(pacer.frames_in_flight() == 1);
    }

    // Tokens: only one that was never handed out is refused. Null, stale and repeated ones are accepted and reported
    // as such, so a title that presents with Token = 0, twice with one token, or out of order does not freeze.
    {
        using Issue = FramePacer::TokenIssue;
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 4));
        std::uint64_t t[3] = {};
        for (auto& token : t) {
            clock.advance_us(period);
            EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok);
        }
        EXPECT(t[0] == 1 && t[1] == 2 && t[2] == 3);
        Issue issue = Issue::stale;
        EXPECT(pacer.check_present_token(4, &issue) == pacer_hr::invalid_arg);     // never issued
        EXPECT(pacer.check_present_token(1000, &issue) == pacer_hr::invalid_arg);
        EXPECT(pacer.check_present_token(0, &issue) == pacer_hr::ok && issue == Issue::null_token);
        EXPECT(pacer.check_present_token(2, &issue) == pacer_hr::ok && issue == Issue::none);  // 1 is skipped
        pacer.commit_present_token(2, 1);
        EXPECT(pacer.check_present_token(2, &issue) == pacer_hr::ok && issue == Issue::repeated);
        EXPECT(pacer.check_present_token(1, &issue) == pacer_hr::ok && issue == Issue::stale);
        EXPECT(pacer.check_present_token(3, &issue) == pacer_hr::ok && issue == Issue::none);
        // Presenting a stale or null token does not move the last presented token back.
        pacer.commit_present_token(1, 1);
        pacer.commit_present_token(0, 1);
        EXPECT(pacer.check_present_token(2, &issue) == pacer_hr::ok && issue == Issue::repeated);
        EXPECT(pacer.check_present_token(3, nullptr) == pacer_hr::ok);
    }

    // Changing the interval while the origin event is armed and a waiter is blocked: the event stays armed, the grid
    // is re-based on the change, and the waiter comes back at the first origin of the new grid.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1));
        auto waiter = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(waiter));
        clock.advance_us(period / 2);
        EXPECT(pacer.set_frame_interval(2 * period, 1, 0) == pacer_hr::ok);  // now = period / 2; new grid from here
        EXPECT(pacer.registered() && pacer.period_us() == 2 * period);
        clock.advance_us(period);  // the old grid's first origin has passed: still blocked
        EXPECT(blocked(waiter));
        clock.advance_us(period - 1);
        EXPECT(blocked(waiter));
        clock.advance_us(1);  // period / 2 + 2 * period: the first origin of the new grid
        Result r = waiter.get();
        EXPECT(r.hr == pacer_hr::ok && r.token == 1);
        std::uint32_t count = 1;
        FrameStatistic stat;
        EXPECT(pacer.get_frame_statistics(1, stat_frame_event, &count, &stat) == pacer_hr::ok);
        EXPECT(stat.time_us == period / 2 + 2 * period);
        // An offset larger than the new, shorter period is cut to fit.
        EXPECT(pacer.schedule_frame_event(frame_event_origin, period + 1000, 0) == pacer_hr::ok);
        EXPECT(pacer.set_frame_interval(period / 2, 1, 0) == pacer_hr::ok);
        EXPECT(pacer.registered());
    }

    // Scheduling inside a period, before the offset has passed: that period's origin is still to come.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(pacer.set_frame_interval(period, 1, 0) == pacer_hr::ok);
        clock.advance_us(period * 2 + 2000);
        EXPECT(pacer.schedule_frame_event(frame_event_origin, 5000, 0) == pacer_hr::ok);
        auto wait = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(wait));
        clock.advance_us(2999);
        EXPECT(blocked(wait));
        clock.advance_us(1);  // 2 * period + 5000
        EXPECT(wait.get().token == 1);
        std::uint32_t count = 1;
        FrameStatistic stat;
        EXPECT(pacer.get_frame_statistics(1, stat_frame_event, &count, &stat) == pacer_hr::ok);
        EXPECT(stat.time_us == period * 2 + 5000);
    }

    // Statistics: unknown tokens and types the pacer cannot give report S_FALSE and count 0.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1));
        clock.advance_us(period);
        std::uint64_t token = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok);
        std::uint32_t count = 4;
        FrameStatistic stats[4];
        EXPECT(pacer.get_frame_statistics(77, stat_frame_event, &count, stats) == pacer_hr::false_ && count == 0);
        count = 4;
        EXPECT(pacer.get_frame_statistics(token, stat_render | stat_frame_interval, &count, stats) == pacer_hr::false_ &&
               count == 0);
        // Not presented yet: the event is there, present and display are not.
        count = 4;
        EXPECT(pacer.get_frame_statistics(token, stat_frame_event | stat_present | stat_display, &count, stats) ==
                   pacer_hr::false_ && count == 1 && stats[0].type == stat_frame_event);
        count = 0;
        EXPECT(pacer.get_frame_statistics(token, stat_frame_event, nullptr, stats) == pacer_hr::invalid_arg);
        // A null array only counts.
        count = 4;
        EXPECT(pacer.get_frame_statistics(token, stat_frame_event, &count, nullptr) == pacer_hr::ok && count == 1);
        // Presented but not shown: display missing. Then shown.
        pacer.commit_present_token(token, 1);
        count = 4;
        EXPECT(pacer.get_frame_statistics(token, stat_present | stat_display, &count, stats) == pacer_hr::false_ &&
               count == 1 && stats[0].type == stat_present);
        clock.advance_us(period);
        count = 4;
        EXPECT(pacer.get_frame_statistics(token, stat_present | stat_display, &count, stats) == pacer_hr::ok &&
               count == 2);
        // Too little room: written up to the room, S_FALSE.
        count = 1;
        EXPECT(pacer.get_frame_statistics(token, stat_present | stat_display, &count, stats) == pacer_hr::false_ &&
               count == 1);
    }

    // Suspend: a blocked waiter fails, presents are refused, registration is gone after resume and must be redone;
    // tokens keep counting.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1));
        clock.advance_us(period);
        std::uint64_t token = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok && token == 1);
        auto waiter = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(waiter));
        pacer.suspend();
        EXPECT(waiter.get().hr == pacer_hr::invalid_state);
        EXPECT(pacer.suspended());
        EXPECT(pacer.check_present_token(1) == pacer_hr::invalid_state);
        EXPECT(pacer.set_frame_interval(period, 1, 0) == pacer_hr::invalid_state);
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::invalid_state);
        pacer.resume();
        EXPECT(!pacer.suspended() && !pacer.registered());
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::invalid_state);  // not re-registered
        EXPECT(pacer.schedule_frame_event(frame_event_origin, 0, 0) == pacer_hr::invalid_state);       // interval too
        EXPECT(setup(pacer, 1));
        clock.advance_us(period);
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok && token == 2);
        EXPECT(pacer.check_present_token(2) == pacer_hr::ok);
        EXPECT(pacer.check_present_token(1) == pacer_hr::ok);  // never presented, so not stale
    }

    // Destroying a pacer with a blocked waiter lets the waiter go.
    {
        ManualFrameClock clock;
        std::future<Result> waiter;
        {
            FramePacer pacer(clock);
            EXPECT(setup(pacer, 1));
            waiter = wait_async(pacer, infinite_timeout_ms);
            EXPECT(blocked(waiter));
        }
        EXPECT(waiter.get().hr == pacer_hr::invalid_state);
    }

    // ImmediateThresholdPercent: the pure decision (late_by, interval, threshold -> mode).
    {
        const auto sync = [](std::uint64_t late, std::uint32_t interval, float threshold) {
            return decide_present_sync(late, interval, threshold);
        };
        constexpr PresentSync v = PresentSync::vblank, i = PresentSync::immediate;
        const float nan = std::numeric_limits<float>::quiet_NaN();
        EXPECT(sync(0, period, 100.0f) == v);                  // on time: nothing to tear
        EXPECT(sync(100, period, 0.0f) == v);                  // threshold 0 disables tearing
        EXPECT(sync(100, period, -5.0f) == v);
        EXPECT(sync(100, period, nan) == v);
        EXPECT(sync(100, 0, 100.0f) == v);                     // unpaced
        EXPECT(sync(1, period, 1.0f) == i);                    // 0.00625% scanned, within 1%
        EXPECT(sync(159, period, 1.0f) == i);                  // 0.99375%
        EXPECT(sync(160, period, 1.0f) == i);                  // exactly 1%: the threshold itself still tears
        EXPECT(sync(161, period, 1.0f) == v);                  // just past it
        EXPECT(sync(8000, period, 49.0f) == v);                // 50% scanned
        EXPECT(sync(8000, period, 50.0f) == i);
        EXPECT(sync(8000, period, 51.0f) == i);
        EXPECT(sync(15999, period, 100.0f) == i);              // 100: anywhere in the scan
        EXPECT(sync(15999, period, 250.0f) == i);              // above 100 is clamped
        EXPECT(sync(15999, period, 99.0f) == v);
        EXPECT(sync(period + 4000, period, 25.0f) == i);       // a whole interval and a quarter late: the scan is at 25%
        EXPECT(sync(period + 4000, period, 24.0f) == v);
        EXPECT(sync(3 * period, period, 10.0f) == i);          // late by whole intervals: the scan is at the top
        EXPECT(scan_percent(8000, period) == 50.0f);
        EXPECT(scan_percent(0, period) == 0.0f && scan_percent(5, 0) == 0.0f);
    }

    // The same through the pacer: lateness against the vblank the token was due at, and what a tear does to the frame.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 2));
        clock.advance_us(period);  // origin 1; its frame is due at vblank 2 (t = 2 * period)
        std::uint64_t token1 = 0, token2 = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token1) == pacer_hr::ok);
        FramePacer::PresentTiming timing = pacer.present_timing(token1);
        EXPECT(timing.period_us == period && timing.late_us == 0);  // before the vblank it is due at
        EXPECT(pacer.present_timing(0).late_us == 0 && pacer.present_timing(99).late_us == 0);  // null, unknown
        clock.advance_us(period);  // exactly at its vblank: not late
        EXPECT(pacer.present_timing(token1).late_us == 0);
        clock.advance_us(8000);    // half an interval late
        timing = pacer.present_timing(token1);
        EXPECT(timing.late_us == 8000);
        const PresentSync mode = decide_present_sync(timing.late_us, timing.period_us, 60.0f);
        EXPECT(mode == PresentSync::immediate);
        pacer.commit_present_token(token1, 1, mode);
        EXPECT(pacer.immediate_presents() == 1);
        EXPECT(pacer.present_timing(token1).late_us == 0);  // presented already: a repeated token never tears
        EXPECT(pacer.frames_in_flight() == 0);  // shown at once, not queued for the next vblank
        std::uint32_t count = 4;
        FrameStatistic stats[4];
        EXPECT(pacer.get_frame_statistics(token1, stat_present | stat_display, &count, stats) == pacer_hr::ok && count == 2);
        EXPECT(stats[0].type == stat_present && stats[1].type == stat_display);
        EXPECT(stats[1].time_us == stats[0].time_us && stats[1].time_us == 2 * period + 8000);
        EXPECT(stats[1].percent_scanned == 50.0f && stats[0].percent_scanned == 0.0f);

        // The next frame, late by the same amount but with a threshold that is too small: waits for its vblank.
        clock.advance_us(period - 8000);  // t = 3 * period: origin 3
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token2) == pacer_hr::ok);
        clock.advance_us(period + 8000);  // due at 4 * period, now 4.5
        timing = pacer.present_timing(token2);
        EXPECT(timing.late_us == 8000);
        EXPECT(decide_present_sync(timing.late_us, timing.period_us, 40.0f) == PresentSync::vblank);
        pacer.commit_present_token(token2, 1, PresentSync::vblank);
        EXPECT(pacer.immediate_presents() == 1 && pacer.frames_in_flight() == 1);
        EXPECT(pacer.get_frame_statistics(token2, stat_display, &count, stats) == pacer_hr::false_);  // not shown yet
        clock.advance_us(period);  // vblank at 5 * period
        count = 4;
        EXPECT(pacer.get_frame_statistics(token2, stat_display, &count, stats) == pacer_hr::ok && count == 1);
        EXPECT(stats[0].time_us == 5 * period && stats[0].percent_scanned == 0.0f);
    }

    // Rescheduling changes future origins but not the vblank an issued token was due at.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 1, period / 4));
        clock.advance_us(period + period / 4);
        std::uint64_t token = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok);
        EXPECT(pacer.schedule_frame_event(frame_event_origin, period / 2, 0) == pacer_hr::ok);

        clock.advance_us(3 * period / 4);  // vblank 2, still the deadline of the original token
        EXPECT(pacer.present_timing(token).late_us == 0);
        clock.advance_us(period / 4);
        const auto timing = pacer.present_timing(token);
        EXPECT(timing.late_us == period / 4);
        const auto sync = decide_present_sync(timing.late_us, timing.period_us, 30.0f);
        EXPECT(sync == PresentSync::immediate);
        pacer.commit_present_token(token, 1, sync);

        FrameStatistic display{};
        std::uint32_t count = 1;
        EXPECT(pacer.get_frame_statistics(token, stat_display, &count, &display) == pacer_hr::ok);
        EXPECT(count == 1 && display.percent_scanned == 25.0f);
    }

    // A frame behind one that still waits for its vblank cannot be shown at once (it would overtake it): not late.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 2));
        std::uint64_t token1 = 0, token2 = 0;
        clock.advance_us(period);      // origin 1
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token1) == pacer_hr::ok);
        clock.advance_us(period);      // origin 2 (nothing is in flight yet, so the second token is handed out)
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token2) == pacer_hr::ok);
        clock.advance_us(period + 1600);  // 3.1 periods: token1 is presented, a whole interval late, queued for vblank 4
        pacer.commit_present_token(token1, 1);
        EXPECT(pacer.frames_in_flight() == 1);
        clock.advance_us(1600);        // 3.2: token2 was due at vblank 3 and is late, but token1 is still queued
        EXPECT(pacer.present_timing(token2).late_us == 0 && pacer.present_timing(token2).period_us == period);
        clock.advance_us(period);      // 4.2: token1 was shown at vblank 4; token2 is late and nothing is queued
        EXPECT(pacer.present_timing(token2).late_us == 19200);  // 4.2 periods now, due at 3
        EXPECT(pacer.immediate_presents() == 0);
    }

    // Presents made after SetFrameIntervalX but before the origin event is scheduled are unpaced (PresentX does not
    // hold them for a vblank), so they are shown at once and are not frames in flight: however many there were, the
    // first wait returns at the first origin.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(pacer.set_frame_interval(period, 1, 0) == pacer_hr::ok);
        EXPECT(!pacer.registered());
        for (int i = 0; i < 1000; ++i) pacer.note_untokened_present();  // blank presents of a loading screen
        pacer.commit_present_token(0, 1);                               // and one with the null token
        EXPECT(pacer.frames_in_flight() == 0);
        EXPECT(pacer.schedule_frame_event(frame_event_origin, 0, 0) == pacer_hr::ok);
        auto wait = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(wait));
        clock.advance_us(period);
        if (wait.wait_for(std::chrono::seconds(10)) != std::future_status::ready) {
            pacer.suspend();  // lets the waiter go, so that the failure below ends the test and does not hang it
            EXPECT(!"the first wait returns one period after the event is armed");
        }
        Result r = wait.get();
        EXPECT(r.hr == pacer_hr::ok && r.token == 1);
        // Once the event is armed, a present is a frame in flight again.
        pacer.note_untokened_present();
        EXPECT(pacer.frames_in_flight() == 1);
    }

    // Uncapped (LEONIDA_PRESENT_UNCAP): a wait hands a token out at once instead of at the next interval origin,
    // and a presented frame counts as shown at once. Registration and suspension keep their errors.
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        pacer.set_uncapped(true);
        EXPECT(pacer.uncapped());
        std::uint64_t token = 99;
        // Still invalid_state before registration, after suspend, and after resume until re-registered.
        EXPECT(pacer.wait_frame_event(frame_event_origin, infinite_timeout_ms, 0, &token) == pacer_hr::invalid_state &&
               token == 99);
        EXPECT(setup(pacer, 1));
        // Tokens at once, no clock advance; as many in a row as asked (the latency limit does not throttle),
        // each from a wait that would still be blocked without the flag.
        for (std::uint64_t i = 1; i <= 3; ++i) {
            auto wait = wait_async(pacer, infinite_timeout_ms);
            Result r = wait.get();
            EXPECT(r.hr == pacer_hr::ok && r.token == i);
        }
        pacer.commit_present_token(1, 1);
        pacer.commit_present_token(2, 1);
        pacer.commit_present_token(3, 1);
        EXPECT(pacer.frames_in_flight() == 0);  // nothing waits for a vblank
        std::uint32_t count = 2;
        FrameStatistic stats[2];
        EXPECT(pacer.get_frame_statistics(3, stat_present | stat_display, &count, stats) == pacer_hr::ok);
        EXPECT(count == 2 && stats[0].type == stat_present && stats[0].time_us == 0 &&
               stats[1].type == stat_display && stats[1].time_us == 0);
        // An untokened (blank) present is not queued on the grid either.
        pacer.note_untokened_present();
        EXPECT(pacer.frames_in_flight() == 0);
        // An uncapped frame is due at once: present_timing reports no lateness.
        const FramePacer::PresentTiming timing = pacer.present_timing(3);
        EXPECT(timing.late_us == 0 && timing.period_us == 0);
        pacer.suspend();
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::invalid_state);
        pacer.resume();
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::invalid_state);
        EXPECT(setup(pacer, 1));
        clock.advance_us(1234);
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok && token == 4);
        // The flag survives suspend/resume (set once, by the environment).
        pacer.suspend();
        pacer.resume();
        EXPECT(pacer.uncapped());
        EXPECT(setup(pacer, 1));
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &token) == pacer_hr::ok && token == 5);
        // Turning the cap back on restores the grid: the next wait blocks until the next origin (the grid was
        // re-based at the last SetFrameIntervalX, so a whole period is the next origin).
        pacer.set_uncapped(false);
        auto wait = wait_async(pacer, infinite_timeout_ms);
        EXPECT(blocked(wait));
        clock.advance_us(period);
        EXPECT(wait.get().token == 6);
    }

    // Real clock: five 10 ms vblanks take about 50 ms.
    {
        SteadyFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(pacer.set_frame_interval(10000, 1, 0) == pacer_hr::ok);
        EXPECT(pacer.schedule_frame_event(frame_event_origin, 0, 0) == pacer_hr::ok);
        const auto start = std::chrono::steady_clock::now();
        std::uint64_t token = 0, last = 0;
        for (int i = 0; i < 5; ++i) {
            EXPECT(pacer.wait_frame_event(frame_event_origin, 2000, 0, &token) == pacer_hr::ok);
            EXPECT(token == last + 1);
            last = token;
        }
        const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start);
        std::printf("real clock: 5 origins at 10 ms took %lld ms\n", static_cast<long long>(ms.count()));
        EXPECT(ms.count() >= 45 && ms.count() < 1000);
    }

    // TOKEN_ON_TIMEOUT (flag 2) and multi-event pacing (origin poll + blocking flip/vblank wait).
    {
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(pacer.set_frame_interval(period, 3, 0) == pacer_hr::ok);
        EXPECT(pacer.schedule_frame_event(frame_event_origin, 0, 0) == pacer_hr::ok);
        EXPECT(pacer.schedule_frame_event(frame_event_vblank, 0, 0) == pacer_hr::ok);
        EXPECT(pacer.schedule_frame_event(frame_event_flip, 0, 0) == pacer_hr::ok);

        // Origin poll with timeout 0 and TOKEN_ON_TIMEOUT issues a consecutive token and returns S_FALSE.
        std::uint64_t tok1 = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, frame_wait_flag_token_on_timeout, &tok1) ==
               pacer_hr::false_);
        EXPECT(tok1 == 1);
        EXPECT(pacer.check_present_token(tok1) == pacer_hr::ok);

        // A blocking flip wait paces to the next tick and preserves the caller's token unchanged.
        auto flip_wait = std::async(std::launch::async, [&] {
            std::uint64_t preserved = tok1;
            const std::int32_t hr = pacer.wait_frame_event(frame_event_flip, infinite_timeout_ms, 0, &preserved);
            return Result{hr, preserved};
        });
        EXPECT(blocked(flip_wait));
        clock.advance_us(period);
        const Result fr = flip_wait.get();
        EXPECT(fr.hr == pacer_hr::ok && fr.token == tok1);

        // Second flip wait blocks for the following tick; vblank wait on tick 1 succeeds immediately (independent).
        EXPECT(pacer.wait_frame_event(frame_event_vblank, 0, 0, nullptr) == pacer_hr::ok);
        auto flip_wait2 = std::async(std::launch::async, [&] {
            return Result{pacer.wait_frame_event(frame_event_flip, infinite_timeout_ms, 0, nullptr), 0};
        });
        EXPECT(blocked(flip_wait2));
        clock.advance_us(period);
        EXPECT(flip_wait2.get().hr == pacer_hr::ok);

        // Origin wait at tick 2 advances consecutively after the early-started token 1.
        std::uint64_t tok2 = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &tok2) == pacer_hr::ok && tok2 == 2);
    }

    // BL-6: frame pacing drift reporting (p50/p95/p99 frame times, mean drift vs target interval,
    // origin-to-present latency, ring-buffer wrap-around, suspend/resume gap exclusion, and uncapped mode).
    {
        auto near = [](double a, double b) { return (a > b ? a - b : b - a) < 1e-6; };

        // 1) Empty and single-frame edge cases.
        ManualFrameClock clock;
        FramePacer pacer(clock);
        EXPECT(setup(pacer, 2));
        FramePacingSummary s0 = pacer.pacing_summary();
        EXPECT(s0.frames == 0 && s0.samples == 0 && s0.target_interval_us == period);
        EXPECT(near(s0.mean_us, 0.0) && near(s0.drift_us, 0.0));
        EXPECT(s0.p50_us == 0 && s0.p95_us == 0 && s0.p99_us == 0 && s0.latency_samples == 0);

        clock.advance_us(period);
        std::uint64_t tok = 0;
        EXPECT(pacer.wait_frame_event(frame_event_origin, 0, 0, &tok) == pacer_hr::ok);
        clock.advance_us(2000);
        pacer.commit_present_token(tok, 1);
        FramePacingSummary s1 = pacer.pacing_summary();
        EXPECT(s1.frames == 1 && s1.samples == 0 && s1.latency_samples == 1 && s1.latency_p50_us == 2000);
        EXPECT(near(s1.mean_us, 0.0) && near(s1.drift_us, 0.0));

        // 2) 100 known intervals (16000 + 100*i us for i = 1..100) and known origin->present latencies:
        //    mean = 16000 + 5050 = 21050.0 us, drift = +5050.0 us,
        //    nearest-rank p50 = 16000 + 50*100 = 21000, p95 = 25500, p99 = 25900.
        ManualFrameClock clock2;
        FramePacer pacer2(clock2);
        EXPECT(setup(pacer2, 4));
        // Present an anchor frame first at t = period + 1000 (latency = 1000 us).
        clock2.advance_us(period);
        std::uint64_t t = 0;
        EXPECT(pacer2.wait_frame_event(frame_event_origin, 0, 0, &t) == pacer_hr::ok);
        clock2.advance_us(1000);
        pacer2.commit_present_token(t, 1);
        for (std::uint64_t i = 1; i <= 100; ++i) {
            const std::uint64_t dt = period + i * 100;
            clock2.advance_us(dt);
            // Issue token after dt - (i * 50)? Simpler: issue token at origin (which passed), present immediately.
            std::uint64_t tok_i = 0;
            EXPECT(pacer2.wait_frame_event(frame_event_origin, 0, 0, &tok_i) == pacer_hr::ok);
            pacer2.commit_present_token(tok_i, 1);
        }
        const FramePacingSummary s100 = pacer2.pacing_summary();
        EXPECT(s100.frames == 101 && s100.samples == 100 && s100.target_interval_us == period);
        EXPECT(near(s100.mean_us, 21050.0));
        EXPECT(near(s100.drift_us, 5050.0));
        EXPECT(s100.p50_us == 21000 && s100.p95_us == 25500 && s100.p99_us == 25900);
        EXPECT(s100.latency_samples == 101);

        // 3) Negative drift (untokened/early presents faster than target period) + exact origin-to-present latency.
        ManualFrameClock clock3;
        FramePacer pacer3(clock3);
        EXPECT(setup(pacer3, 4));
        clock3.advance_us(period);
        EXPECT(pacer3.wait_frame_event(frame_event_origin, 0, 0, &t) == pacer_hr::ok);
        clock3.advance_us(3000);  // latency = 3000
        pacer3.commit_present_token(t, 1);
        clock3.advance_us(10000);  // dt = 10000 (< 16000) -> drift = -6000
        pacer3.note_untokened_present();
        const FramePacingSummary sneg = pacer3.pacing_summary();
        EXPECT(sneg.frames == 2 && sneg.samples == 1 && sneg.target_interval_us == period);
        EXPECT(near(sneg.mean_us, 10000.0) && near(sneg.drift_us, -6000.0));
        EXPECT(sneg.p50_us == 10000 && sneg.p95_us == 10000 && sneg.p99_us == 10000);
        EXPECT(sneg.latency_samples == 1 && sneg.latency_p50_us == 3000 && sneg.latency_p99_us == 3000);

        // 4) Suspend/resume gap is excluded from frame intervals.
        pacer3.suspend();
        clock3.advance_us(5'000'000);  // 5 second pause while suspended
        pacer3.resume();
        EXPECT(setup(pacer3, 2));
        clock3.advance_us(period);
        EXPECT(pacer3.wait_frame_event(frame_event_origin, 0, 0, &t) == pacer_hr::ok);
        pacer3.commit_present_token(t, 1);
        const FramePacingSummary ssusp = pacer3.pacing_summary();
        EXPECT(ssusp.frames == 3 && ssusp.samples == 1 && ssusp.p99_us == 10000);  // 5s gap not recorded

        // 5) Ring-buffer wrap-around: fill with slow 32000 us frames, then overwrite entire window with steady 16000 us.
        ManualFrameClock clock4;
        FramePacer pacer4(clock4);
        EXPECT(setup(pacer4, 2));
        clock4.advance_us(period);
        EXPECT(pacer4.wait_frame_event(frame_event_origin, 0, 0, &t) == pacer_hr::ok);
        pacer4.commit_present_token(t, 1);
        for (std::size_t i = 0; i < frame_pacing_window; ++i) {
            clock4.advance_us(period * 2);
            EXPECT(pacer4.wait_frame_event(frame_event_origin, 0, 0, &t) == pacer_hr::ok);
            pacer4.commit_present_token(t, 1);
        }
        const FramePacingSummary slow = pacer4.pacing_summary();
        EXPECT(slow.samples == frame_pacing_window && near(slow.mean_us, double(period * 2)) &&
               near(slow.drift_us, double(period)));
        for (std::size_t i = 0; i < frame_pacing_window; ++i) {
            clock4.advance_us(period);
            EXPECT(pacer4.wait_frame_event(frame_event_origin, 0, 0, &t) == pacer_hr::ok);
            pacer4.commit_present_token(t, 1);
        }
        const FramePacingSummary steady = pacer4.pacing_summary();
        EXPECT(steady.frames == 1 + 2 * frame_pacing_window && steady.samples == frame_pacing_window);
        EXPECT(near(steady.mean_us, double(period)) && near(steady.drift_us, 0.0));
        EXPECT(steady.p50_us == period && steady.p95_us == period && steady.p99_us == period);

        // 6) Uncapped mode: target_interval_us == 0 and drift_us == 0 while intervals are still tracked.
        pacer4.set_uncapped(true);
        const FramePacingSummary uncap = pacer4.pacing_summary();
        EXPECT(uncap.target_interval_us == 0 && near(uncap.drift_us, 0.0) && uncap.p50_us == period);
    }

    std::printf("frame_pacer_test: all cases pass\n");
    return 0;
}
