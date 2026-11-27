// condition_variable timed waits release their mutex (src/crt_timed_wait.cpp, #3796). On Wine 9.0 the STL's
// statically linked timed wait kept the mutex for the whole timeout, so a thread setting the predicate waited until
// the wait timed out. Also checks the other definitions that file takes over: std::shared_mutex and
// this_thread::sleep_for. No game files, no GPU.
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <mutex>
#include <shared_mutex>
#include <thread>

namespace {
int failures = 0;

void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) ++failures;
}

using Clock = std::chrono::steady_clock;

long long ms_since(Clock::time_point start) {
    return std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - start).count();
}
}  // namespace

int main() {
    std::mutex mutex;
    std::condition_variable cv;

    {
        // The waiter holds the mutex only until it waits: a second thread takes it, sets the flag and notifies.
        bool flag = false;
        long long took_lock_ms = -1;
        const auto start = Clock::now();
        std::thread setter([&] {
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
            const auto before = Clock::now();
            {
                std::lock_guard<std::mutex> lock(mutex);
                took_lock_ms = ms_since(before);
                flag = true;
            }
            cv.notify_all();
        });
        bool woke = false;
        {
            std::unique_lock<std::mutex> lock(mutex);
            woke = cv.wait_for(lock, std::chrono::seconds(5), [&] { return flag; });
        }
        const long long waited = ms_since(start);
        setter.join();
        std::printf("  wait_for(5 s) returned after %lld ms; the setter waited %lld ms for the mutex\n", waited,
                    took_lock_ms);
        check(woke, "wait_for sees the predicate another thread set");
        check(took_lock_ms >= 0 && took_lock_ms < 1000, "the mutex is free while wait_for waits");
        check(waited < 2500, "wait_for is woken, not timed out");
    }

    {
        // wait_until goes through the same timed wait.
        bool flag = false;
        std::thread setter([&] {
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
            { std::lock_guard<std::mutex> lock(mutex); flag = true; }
            cv.notify_one();
        });
        const auto start = Clock::now();
        bool woke = false;
        {
            std::unique_lock<std::mutex> lock(mutex);
            woke = cv.wait_until(lock, Clock::now() + std::chrono::seconds(5), [&] { return flag; });
        }
        const long long waited = ms_since(start);
        setter.join();
        check(woke && waited < 2500, "wait_until is woken, not timed out");
    }

    {
        // Nobody notifies: the wait times out after about its timeout, with the mutex held again.
        const auto start = Clock::now();
        std::unique_lock<std::mutex> lock(mutex);
        const std::cv_status status = cv.wait_for(lock, std::chrono::milliseconds(200));
        const long long waited = ms_since(start);
        std::printf("  an unnotified wait_for(200 ms) returned after %lld ms\n", waited);
        check(status == std::cv_status::timeout, "an unnotified wait_for times out");
        check(waited >= 150 && waited < 2000, "an unnotified wait_for lasts about its timeout");
        check(lock.owns_lock(), "the mutex is held again after the timeout");
        bool other_got_it = true;
        std::thread probe([&] { other_got_it = mutex.try_lock(); if (other_got_it) mutex.unlock(); });
        probe.join();
        check(!other_got_it, "another thread cannot take it then");
    }

    {
        std::shared_mutex shared;
        shared.lock_shared();
        check(shared.try_lock_shared(), "shared_mutex: two shared owners");
        bool exclusive = true;
        std::thread probe([&] { exclusive = shared.try_lock(); if (exclusive) shared.unlock(); });
        probe.join();
        check(!exclusive, "shared_mutex: no exclusive owner while shared");
        shared.unlock_shared();
        shared.unlock_shared();
        check(shared.try_lock(), "shared_mutex: exclusive once released");
        shared.unlock();
    }

    {
        const auto start = Clock::now();
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        check(ms_since(start) >= 90, "this_thread::sleep_for sleeps");
    }

    {
        // Repeated short unnotified waits must not return cv_status::no_timeout early before their deadline.
        bool no_early_no_timeout = true;
        bool all_reached_target = true;
        std::unique_lock<std::mutex> lock(mutex);
        for (int i = 0; i < 25; ++i) {
            const auto target = Clock::now() + std::chrono::milliseconds(10);
            const std::cv_status status = cv.wait_for(lock, std::chrono::milliseconds(10));
            const auto finished = Clock::now();
            if (finished < target) {
                all_reached_target = false;
                if (status == std::cv_status::no_timeout) no_early_no_timeout = false;
            }
        }
        check(no_early_no_timeout, "25 short unnotified wait_for calls never return no_timeout before target");
        check(all_reached_target, "25 short unnotified wait_for calls reach steady_clock target");
    }

#ifdef _WIN32
    {
        // A pre-existing ERROR_TIMEOUT must not misclassify a real notification as a timeout and must be preserved.
        bool flag = false;
        std::thread setter([&] {
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
            { std::lock_guard<std::mutex> lock(mutex); flag = true; }
            cv.notify_one();
        });
        SetLastError(ERROR_TIMEOUT);
        std::cv_status status = std::cv_status::timeout;
        {
            std::unique_lock<std::mutex> lock(mutex);
            while (!flag) {
                status = cv.wait_for(lock, std::chrono::seconds(5));
                if (status == std::cv_status::timeout) break;
            }
        }
        const DWORD last_error = GetLastError();
        setter.join();
        check(flag && status == std::cv_status::no_timeout, "pre-existing ERROR_TIMEOUT does not mask notification");
        check(last_error == ERROR_TIMEOUT, "pre-existing ERROR_TIMEOUT is preserved across wait_for");
    }

    {
        // Caller LastError is preserved across a timed-out wait_for.
        SetLastError(ERROR_INVALID_DATA);
        std::unique_lock<std::mutex> lock(mutex);
        const auto target = Clock::now() + std::chrono::milliseconds(15);
        const std::cv_status status = cv.wait_for(lock, std::chrono::milliseconds(15));
        const auto finished = Clock::now();
        const DWORD last_error = GetLastError();
        check(status == std::cv_status::timeout || finished >= target,
              "unnotified wait_for with preset LastError does not return no_timeout before target");
        check(finished >= target, "unnotified wait_for with preset LastError reaches steady_clock target");
        check(last_error == ERROR_INVALID_DATA, "caller LastError is preserved across timed-out wait_for");
    }
#endif

    if (failures) {
        std::printf("RESULT: FAIL (%d failures)\n", failures);
        return 1;
    }
    std::printf("RESULT: PASS\n");
    return 0;
}
