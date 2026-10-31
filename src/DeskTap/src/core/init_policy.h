// Decisions without Windows dependencies, unit-tested in tests/core_tests.cpp.
#pragma once
#include <algorithm>
#include <cstdint>

namespace desk::core {

// When the engine (re)initializes XAML diagnostics in its process.
//
// - Only while styling is wanted: an engine loaded while Desk is off (an
//   injection that was already under way) stays dormant.
// - One attempt at a time; the caller reports how it ended.
// - Failures back off (15 s, 30 s, 60 s ... up to 5 minutes) and are retried
//   for as long as the process lives.
// - A recover request from Desk.exe ("re-apply") retries a failed
//   initialization right away. The first token seen only sets the baseline.
class InitPolicy {
public:
    enum class State { Idle, Running, Succeeded, Failed };

    static constexpr uint64_t kFirstBackoffMs = 15'000;
    static constexpr uint64_t kMaxBackoffMs = 300'000;

    State state() const { return m_state; }
    uint32_t attempts() const { return m_attempts; }
    uint32_t failures() const { return m_failures; }
    uint64_t nextAttemptMs() const { return m_nextAttemptMs; }

    bool Due(uint64_t nowMs, bool wanted) const {
        if (!wanted) {
            return false;
        }
        switch (m_state) {
            case State::Idle:
                return true;
            case State::Failed:
                return nowMs >= m_nextAttemptMs;
            default:
                return false;
        }
    }

    // Returns false when an attempt is already running or done.
    bool OnStarted() {
        if (m_state == State::Running || m_state == State::Succeeded) {
            return false;
        }
        m_state = State::Running;
        m_attempts++;
        return true;
    }

    void OnSucceeded() {
        m_state = State::Succeeded;
        m_failures = 0;
    }

    void OnFailed(uint64_t nowMs) {
        m_state = State::Failed;
        m_failures++;
        m_nextAttemptMs = nowMs + Backoff(m_failures);
    }

    // Returns true when the request made a failed initialization due now.
    bool OnRecoverRequest(uint64_t token, uint64_t nowMs) {
        if (!m_haveToken) {
            m_haveToken = true;
            m_token = token;
            return false;
        }
        if (token == m_token) {
            return false;
        }
        m_token = token;
        if (m_state != State::Failed) {
            return false;
        }
        m_nextAttemptMs = nowMs;
        return true;
    }

    static uint64_t Backoff(uint32_t failures) {
        uint32_t shift = std::min<uint32_t>(failures > 0 ? failures - 1 : 0, 10);
        return std::min(kFirstBackoffMs << shift, kMaxBackoffMs);
    }

private:
    State m_state = State::Idle;
    uint32_t m_attempts = 0;
    uint32_t m_failures = 0;
    uint64_t m_nextAttemptMs = 0;
    bool m_haveToken = false;
    uint64_t m_token = 0;
};

}  // namespace desk::core
