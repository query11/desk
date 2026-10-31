// Decisions without Windows dependencies, unit-tested in tests/core_tests.cpp.
#pragma once
#include <cstdint>
#include <string>

namespace desk::core {

// Whether the Desk.exe that owns the current configuration is still there.
//
// The owner is named in the configuration (a session id, its process id and
// creation time). Two signals are used:
//
// - The process itself, when it can be opened. Exited, or the id now belongs
//   to another process (creation time differs): definitely gone. Running:
//   there, even if it doesn't respond (a hung Desk.exe is not a dead one; the
//   emergency restore handles that).
// - Otherwise (an app container may not be allowed to open it) the heartbeat
//   Desk.exe writes every few seconds. Only a channel that has worked at least
//   once for this session can declare it gone, after a grace period measured
//   in time the system was awake, so sleep, a slow disk, a locked file or a
//   short stall never count.
//
// Losing the owner is final for its session when the process is known to be
// gone. After a heartbeat timeout, a heartbeat that resumes re-attaches it. A
// configuration naming another session is a new owner (a new Desk.exe, or
// the emergency restore) and starts over; nothing decided for an older
// session carries over to it.
class HostWatch {
public:
    enum class Probe { Alive, Gone, Unknown };
    enum class Verdict { Unowned, Attached, Lost };
    enum class Reason { None, ProcessGone, NoHeartbeat };

    static constexpr uint64_t kGraceMs = 60'000;

    // The owner named by the current configuration; an empty session means
    // nobody (configurations from older Desk versions, or no host).
    // Returns true when this is a different owner than before.
    bool Attach(const std::wstring& session, uint64_t nowMs) {
        if (session == m_session) {
            return false;
        }
        m_session = session;
        m_verdict = session.empty() ? Verdict::Unowned : Verdict::Attached;
        m_reason = Reason::None;
        m_everBeat = false;
        m_lastSeq = 0;
        m_lastBeatMs = nowMs;
        return true;
    }

    // `beatRead`: the heartbeat file was read and names this session.
    Verdict Observe(Probe probe, bool beatRead, uint64_t beatSeq, uint64_t nowMs) {
        if (m_session.empty()) {
            return m_verdict = Verdict::Unowned;
        }
        if (m_verdict == Verdict::Lost && m_reason == Reason::ProcessGone) {
            return m_verdict;
        }

        bool beat = beatRead && (!m_everBeat || beatSeq != m_lastSeq);
        if (beat) {
            m_everBeat = true;
            m_lastSeq = beatSeq;
            m_lastBeatMs = nowMs;
        }

        switch (probe) {
            case Probe::Gone:
                m_reason = Reason::ProcessGone;
                return m_verdict = Verdict::Lost;
            case Probe::Alive:
                m_reason = Reason::None;
                return m_verdict = Verdict::Attached;
            case Probe::Unknown:
                break;
        }

        if (m_verdict == Verdict::Lost) {
            // Lost on the heartbeat: it beating again means it's back.
            if (beat) {
                m_reason = Reason::None;
                m_verdict = Verdict::Attached;
            }
            return m_verdict;
        }
        if (m_everBeat && nowMs - m_lastBeatMs > kGraceMs) {
            m_reason = Reason::NoHeartbeat;
            return m_verdict = Verdict::Lost;
        }
        return m_verdict = Verdict::Attached;
    }

    Verdict verdict() const { return m_verdict; }
    Reason reason() const { return m_reason; }
    const std::wstring& session() const { return m_session; }
    bool heartbeatSeen() const { return m_everBeat; }

private:
    std::wstring m_session;
    Verdict m_verdict = Verdict::Unowned;
    Reason m_reason = Reason::None;
    bool m_everBeat = false;
    uint64_t m_lastSeq = 0;
    uint64_t m_lastBeatMs = 0;
};

}  // namespace desk::core
