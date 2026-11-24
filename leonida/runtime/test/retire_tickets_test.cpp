// CPU-only regression: independent queues can finish in the opposite order to host submission.
#include "../src/retire_tickets.h"

#include <array>
#include <cstdint>
#include <cstdio>

int main() {
    leonida::RetireTickets<int> tickets;
    std::array<std::uint64_t, 3> done{};
    bool queried_destroyed = false;
    bool queue_one_destroyed = false;
    const auto query = [&](int queue, std::uint64_t value) {
        if (queue == 1 && queue_one_destroyed) queried_destroyed = true;
        return done[queue] >= value;
    };

    tickets.record(1, 1, 1);  // first queue waits on a fence
    tickets.record(2, 2, 1);  // second queue signals it and finishes first
    done[2] = 1;
    if (tickets.completed(query) != 0) return 1;  // ticket 2 cannot retire ticket 1's resources
    done[1] = 1;
    if (tickets.completed(query) != 2) return 2;

    tickets.record(3, 1, 2);
    tickets.record(4, 2, 2);
    done[2] = 2;
    if (tickets.completed(query) != 2) return 3;
    tickets.queue_idle(1);  // its progress semaphore can now be destroyed
    queue_one_destroyed = true;
    if (tickets.completed(query) != 4 || queried_destroyed) return 4;

    // pending_through() must coalesce repeated submissions on the same queue to the highest
    // timeline value, omit idle queues, and exclude tickets past the requested cutoff.
    tickets.record(5, 1, 10);
    tickets.record(6, 2, 20);
    tickets.record(7, 1, 12);
    tickets.record(8, 1, 15);
    tickets.record(9, 2, 25);
    tickets.queue_idle(2);
    const auto wait_list = tickets.pending_through(7);
    if (wait_list.size() != 1 || wait_list[0].first != 1 || wait_list[0].second != 12) return 5;

    // A non-monotonic ticket must not move the completed prefix backward.
    done[1] = 15;
    if (tickets.completed(query) != 9) return 6;
    tickets.record(3, 1, 16);
    done[1] = 16;
    if (tickets.completed(query) != 9) return 7;

    std::puts("out-of-order queue completion kept the retirement prefix safe");
    return 0;
}
