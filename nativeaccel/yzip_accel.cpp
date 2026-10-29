#include "yzip_accel.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <ObjBase.h>
#include <OleAuto.h>

#include "CPP/Common/MyInitGuid.h"
#include "CPP/7zip/Archive/IArchive.h"
#include "C/7zCrc.h"

#include <algorithm>
#include <atomic>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <unordered_map>
#include <vector>

#pragma comment(lib, "Ole32.lib")
#pragma comment(lib, "OleAut32.lib")

namespace {
thread_local std::wstring g_last_error;
thread_local yz_zip_metrics g_last_metrics{};
static_assert(sizeof(std::uint32_t) == sizeof(LONG));
static_assert(sizeof(std::uint64_t) == sizeof(LONG64));

const GUID kZipClassId = {
    0x23170F69,
    0x40C1,
    0x278A,
    {0x10, 0x00, 0x00, 0x01, 0x10, 0x01, 0x00, 0x00},
};
const GUID k7zClassId = {
    0x23170F69, 0x40C1, 0x278A, {0x10, 0x00, 0x00, 0x01, 0x10, 0x07, 0x00, 0x00},
};

using CreateObjectFunction = HRESULT(WINAPI*)(const GUID*, const GUID*, void**);

std::uint64_t performance_counter() noexcept {
    LARGE_INTEGER value{};
    return QueryPerformanceCounter(&value) ? static_cast<std::uint64_t>(value.QuadPart) : 0;
}

class ScopedMetric final {
public:
    explicit ScopedMetric(std::uint64_t* target) noexcept
        : target_(target), started_(performance_counter()) {}
    ~ScopedMetric() {
        const std::uint64_t finished = performance_counter();
        if (finished >= started_) {
            *target_ += finished - started_;
        }
    }

private:
    std::uint64_t* target_;
    std::uint64_t started_;
};

void set_error(const wchar_t* message) {
    g_last_error = message == nullptr ? L"native ZIP extraction failed" : message;
}

void set_error_code(const wchar_t* message, HRESULT code, Int32 operation) {
    wchar_t buffer[192]{};
    _snwprintf_s(buffer, _countof(buffer), _TRUNCATE, L"%ls (HRESULT=0x%08X, operation=%d)",
                 message, static_cast<unsigned>(code), static_cast<int>(operation));
    g_last_error = buffer;
}

bool is_cancelled(const volatile std::uint32_t* flag) {
    if (flag == nullptr) {
        return false;
    }
    auto* value = reinterpret_cast<volatile LONG*>(
        const_cast<volatile std::uint32_t*>(flag));
    return InterlockedCompareExchange(value, 0L, 0L) != 0;
}

bool is_plain_lzma_method(const wchar_t* method) noexcept {
    if (method == nullptr) return false;
    const bool prefix = wcscmp(method, L"LZMA") == 0 || wcscmp(method, L"LZMA2") == 0 ||
                        wcsncmp(method, L"LZMA:", 5) == 0 || wcsncmp(method, L"LZMA2:", 6) == 0;
    return prefix && wcschr(method, L' ') == nullptr && wcschr(method, L'+') == nullptr;
}

enum class TestWriteFault {
    none,
    failure,
    short_write,
};

TestWriteFault configured_test_write_fault() noexcept {
    wchar_t value[16]{};
    const DWORD length = GetEnvironmentVariableW(
        L"YZIP_NATIVE_TEST_WRITE_FAULT", value, static_cast<DWORD>(std::size(value)));
    if (length == 7 && wcscmp(value, L"failure") == 0) {
        return TestWriteFault::failure;
    }
    if (length == 5 && wcscmp(value, L"short") == 0) {
        return TestWriteFault::short_write;
    }
    return TestWriteFault::none;
}

void initialize_crc() {
    static std::once_flag initialized;
    std::call_once(initialized, CrcGenerateTable);
}

class InFileStream final : public IInStream {
public:
    explicit InFileStream(const wchar_t* path) noexcept
        : refs_(1), handle_(CreateFileW(
              path,
              GENERIC_READ,
              FILE_SHARE_READ,
              nullptr,
              OPEN_EXISTING,
              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
              nullptr)) {}

    ~InFileStream() {
        if (handle_ != INVALID_HANDLE_VALUE) {
            CloseHandle(handle_);
        }
    }

    bool valid() const noexcept { return handle_ != INVALID_HANDLE_VALUE; }

    STDMETHOD(QueryInterface)(REFIID iid, void** object) noexcept override {
        if (object == nullptr) {
            return E_POINTER;
        }
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_ISequentialInStream || iid == IID_IInStream) {
            *object = static_cast<IInStream*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHOD_(ULONG, AddRef)() noexcept override { return ++refs_; }

    STDMETHOD_(ULONG, Release)() noexcept override {
        const ULONG remaining = --refs_;
        if (remaining == 0) {
            delete this;
        }
        return remaining;
    }

    STDMETHOD(Read)(void* data, UInt32 size, UInt32* processed_size) noexcept override {
        if (processed_size == nullptr) {
            return E_POINTER;
        }
        *processed_size = 0;
        DWORD read = 0;
        if (!ReadFile(handle_, data, size, &read, nullptr)) {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        *processed_size = read;
        return S_OK;
    }

    STDMETHOD(Seek)(Int64 offset, UInt32 origin, UInt64* new_position) noexcept override {
        LARGE_INTEGER distance{};
        distance.QuadPart = offset;
        LARGE_INTEGER position{};
        if (!SetFilePointerEx(handle_, distance, &position, origin)) {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        if (position.QuadPart < 0) {
            return STG_E_INVALIDFUNCTION;
        }
        if (new_position != nullptr) {
            *new_position = static_cast<UInt64>(position.QuadPart);
        }
        return S_OK;
    }

private:
    std::atomic<ULONG> refs_;
    HANDLE handle_;
};

class CreateOutStream final : public IOutStream {
public:
    CreateOutStream(const wchar_t* path, std::uint64_t limit) noexcept
        : refs_(1), handle_(CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                       FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr)),
          io_failed_(false), position_(0), limit_(limit) {}
    ~CreateOutStream() { if (handle_ != INVALID_HANDLE_VALUE) CloseHandle(handle_); }
    bool valid() const noexcept { return handle_ != INVALID_HANDLE_VALUE; }
    bool io_failed() const noexcept { return io_failed_; }
    STDMETHOD(QueryInterface)(REFIID iid, void** object) noexcept override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_ISequentialOutStream || iid == IID_IOutStream) {
            *object = static_cast<IOutStream*>(this); AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    STDMETHOD_(ULONG, AddRef)() noexcept override { return ++refs_; }
    STDMETHOD_(ULONG, Release)() noexcept override {
        const ULONG left = --refs_; if (left == 0) delete this; return left;
    }
    STDMETHOD(Write)(const void* data, UInt32 size, UInt32* processed) noexcept override {
        if (processed == nullptr) return E_POINTER;
        *processed = 0;
        if (position_ > limit_ || size > limit_ - position_) {
            io_failed_ = true; return HRESULT_FROM_WIN32(ERROR_DISK_FULL);
        }
        DWORD written = 0;
        if (!WriteFile(handle_, data, size, &written, nullptr) || written != size) {
            io_failed_ = true;
            return HRESULT_FROM_WIN32(written == size ? GetLastError() : ERROR_WRITE_FAULT);
        }
        *processed = written;
        position_ += written;
        return S_OK;
    }
    STDMETHOD(Seek)(Int64 offset, UInt32 origin, UInt64* new_position) noexcept override {
        LARGE_INTEGER distance{}; distance.QuadPart = offset;
        LARGE_INTEGER position{};
        if (!SetFilePointerEx(handle_, distance, &position, origin)) {
            io_failed_ = true; return HRESULT_FROM_WIN32(GetLastError());
        }
        if (position.QuadPart < 0) return STG_E_INVALIDFUNCTION;
        if (static_cast<std::uint64_t>(position.QuadPart) > limit_) {
            io_failed_ = true; return HRESULT_FROM_WIN32(ERROR_DISK_FULL);
        }
        position_ = static_cast<std::uint64_t>(position.QuadPart);
        if (new_position != nullptr) *new_position = static_cast<UInt64>(position.QuadPart);
        return S_OK;
    }
    STDMETHOD(SetSize)(UInt64 size) noexcept override {
        if (size > limit_) { io_failed_ = true; return HRESULT_FROM_WIN32(ERROR_DISK_FULL); }
        LARGE_INTEGER distance{}; distance.QuadPart = static_cast<LONGLONG>(size);
        if (!SetFilePointerEx(handle_, distance, nullptr, FILE_BEGIN) || !SetEndOfFile(handle_)) {
            io_failed_ = true; return HRESULT_FROM_WIN32(GetLastError());
        }
        return S_OK;
    }
private:
    std::atomic<ULONG> refs_;
    HANDLE handle_;
    bool io_failed_;
    std::uint64_t position_;
    std::uint64_t limit_;
};

bool source_matches(const yz_zip_create_item& item) noexcept {
    WIN32_FILE_ATTRIBUTE_DATA data{};
    if (item.source_path == nullptr || !GetFileAttributesExW(
            item.source_path, GetFileExInfoStandard, &data)) return false;
    const bool directory = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    if (directory != (item.is_dir != 0)) return false;
    if (!directory) {
        const std::uint64_t size = (static_cast<std::uint64_t>(data.nFileSizeHigh) << 32) |
                                   data.nFileSizeLow;
        if (size != item.size) return false;
    }
    const std::uint64_t file_time = (static_cast<std::uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) |
                                    data.ftLastWriteTime.dwLowDateTime;
    constexpr std::uint64_t epoch = 116444736000000000ULL;
    const std::uint64_t expected = item.modified_ns < 0 ? 0 :
        epoch + static_cast<std::uint64_t>(item.modified_ns) / 100ULL;
    return file_time == expected;
}

class CreateInputStream final : public ISequentialInStream {
public:
    CreateInputStream(const yz_zip_create_item& item, yz_progress_counters* progress,
                      volatile std::uint32_t* cancelled) noexcept
        : refs_(1), handle_(CreateFileW(item.source_path, GENERIC_READ, FILE_SHARE_READ, nullptr,
                                       OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr)),
          expected_(item.size), read_(0), progress_(progress), cancelled_(cancelled) {}
    ~CreateInputStream() { if (handle_ != INVALID_HANDLE_VALUE) CloseHandle(handle_); }
    bool valid() const noexcept { return handle_ != INVALID_HANDLE_VALUE; }
    bool complete() const noexcept { return read_ == expected_; }
    STDMETHOD(QueryInterface)(REFIID iid, void** object) noexcept override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_ISequentialInStream) {
            *object = static_cast<ISequentialInStream*>(this); AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    STDMETHOD_(ULONG, AddRef)() noexcept override { return ++refs_; }
    STDMETHOD_(ULONG, Release)() noexcept override {
        const ULONG left = --refs_; if (left == 0) delete this; return left;
    }
    STDMETHOD(Read)(void* data, UInt32 size, UInt32* processed) noexcept override {
        if (processed == nullptr) return E_POINTER;
        *processed = 0;
        if (is_cancelled(cancelled_)) return E_ABORT;
        DWORD got = 0;
        if (!ReadFile(handle_, data, size, &got, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
        if (read_ > expected_ || got > expected_ - read_) return E_FAIL;
        read_ += got;
        InterlockedExchangeAdd64(reinterpret_cast<volatile LONG64*>(&progress_->completed_bytes), got);
        *processed = got;
        return S_OK;
    }
private:
    std::atomic<ULONG> refs_;
    HANDLE handle_;
    std::uint64_t expected_;
    std::uint64_t read_;
    yz_progress_counters* progress_;
    volatile std::uint32_t* cancelled_;
};

class CreateCallback final : public IArchiveUpdateCallback {
public:
    CreateCallback(const yz_zip_create_item* items, std::size_t count,
                   yz_progress_counters* progress, volatile std::uint32_t* cancelled) noexcept
        : refs_(1), items_(items), count_(count), progress_(progress), cancelled_(cancelled),
          current_(nullptr), failed_(false), io_failed_(false), corrupt_(false) {}
    ~CreateCallback() { release_current(); }
    bool failed() const noexcept { return failed_; }
    bool io_failed() const noexcept { return io_failed_; }
    bool corrupt() const noexcept { return corrupt_; }
    Int32 operation_result() const noexcept { return operation_result_; }
    STDMETHOD(QueryInterface)(REFIID iid, void** object) noexcept override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IArchiveUpdateCallback) {
            *object = static_cast<IArchiveUpdateCallback*>(this);
        } else if (iid == IID_IProgress) {
            *object = static_cast<IProgress*>(this);
        } else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    STDMETHOD_(ULONG, AddRef)() noexcept override { return ++refs_; }
    STDMETHOD_(ULONG, Release)() noexcept override {
        const ULONG left = --refs_; if (left == 0) delete this; return left;
    }
    STDMETHOD(SetTotal)(UInt64 total) noexcept override {
        progress_->total_bytes = total; return is_cancelled(cancelled_) ? E_ABORT : S_OK;
    }
    STDMETHOD(SetCompleted)(const UInt64*) noexcept override {
        return is_cancelled(cancelled_) ? E_ABORT : S_OK;
    }
    STDMETHOD(GetUpdateItemInfo)(UInt32 index, Int32* new_data, Int32* new_props,
                                 UInt32* archive_index) noexcept override {
        if (index >= count_ || new_data == nullptr || new_props == nullptr || archive_index == nullptr)
            return E_INVALIDARG;
        *new_data = 1; *new_props = 1; *archive_index = static_cast<UInt32>(-1); return S_OK;
    }
    STDMETHOD(GetProperty)(UInt32 index, PROPID property, PROPVARIANT* value) noexcept override {
        if (index >= count_ || value == nullptr) return E_INVALIDARG;
        PropVariantInit(value);
        const auto& item = items_[index];
        try {
            if (property == kpidPath) {
                value->vt = VT_BSTR; value->bstrVal = SysAllocString(item.archive_name);
                return value->bstrVal == nullptr ? E_OUTOFMEMORY : S_OK;
            }
            if (property == kpidIsDir) { value->vt = VT_BOOL; value->boolVal = item.is_dir ? VARIANT_TRUE : VARIANT_FALSE; }
            else if (property == kpidSize) { value->vt = VT_UI8; value->uhVal.QuadPart = item.size; }
            else if (property == kpidAttrib) { value->vt = VT_UI4; value->ulVal = item.attributes; }
            else if (property == kpidMTime) {
                constexpr std::uint64_t epoch = 116444736000000000ULL;
                const std::uint64_t ticks = item.modified_ns < 0 ? epoch :
                    epoch + static_cast<std::uint64_t>(item.modified_ns) / 100ULL;
                value->vt = VT_FILETIME;
                value->filetime.dwLowDateTime = static_cast<DWORD>(ticks);
                value->filetime.dwHighDateTime = static_cast<DWORD>(ticks >> 32);
            }
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    STDMETHOD(GetStream)(UInt32 index, ISequentialInStream** stream) noexcept override {
        if (stream == nullptr || index >= count_) return E_INVALIDARG;
        *stream = nullptr; release_current();
        if (is_cancelled(cancelled_)) return E_ABORT;
        const auto& item = items_[index];
        if (!source_matches(item)) { corrupt_ = true; return E_FAIL; }
        if (item.is_dir) return S_OK;
        current_ = new (std::nothrow) CreateInputStream(item, progress_, cancelled_);
        if (current_ == nullptr) return E_OUTOFMEMORY;
        if (!current_->valid()) { io_failed_ = true; release_current(); return E_FAIL; }
        current_->AddRef(); *stream = current_; return S_OK;
    }
    STDMETHOD(SetOperationResult)(Int32 result) noexcept override {
        operation_result_ = result;
        if (result != NArchive::NExtract::NOperationResult::kOK) failed_ = true;
        release_current();
        return failed_ ? E_FAIL : S_OK;
    }
private:
    void release_current() noexcept { if (current_) { current_->Release(); current_ = nullptr; } }
    std::atomic<ULONG> refs_;
    const yz_zip_create_item* items_;
    std::size_t count_;
    yz_progress_counters* progress_;
    volatile std::uint32_t* cancelled_;
    CreateInputStream* current_;
    bool failed_;
    bool io_failed_;
    bool corrupt_;
    Int32 operation_result_ = -1;
};

class StagingOutStream final : public ISequentialOutStream {
public:
    StagingOutStream(
        const yz_zip_job& job,
        const std::uint64_t output_limit,
        yz_progress_counters* progress,
        volatile std::uint32_t* cancellation_flag,
        bool* corrupt,
        bool* io_failed,
        yz_zip_metrics* metrics) noexcept
        : refs_(1),
          handle_(CreateFileW(
              job.staging_path,
              GENERIC_WRITE,
              0,
              nullptr,
              CREATE_ALWAYS,
              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
              nullptr)),
          expected_size_(job.expected_size),
          expected_crc_(job.expected_crc),
          output_limit_(output_limit),
          progress_(progress),
          cancellation_flag_(cancellation_flag),
          corrupt_(corrupt),
          io_failed_(io_failed),
          metrics_(metrics),
          size_(0),
          crc_(CRC_INIT_VAL),
          test_write_fault_(configured_test_write_fault()) {
        initialize_crc();
    }

    ~StagingOutStream() {
        if (handle_ != INVALID_HANDLE_VALUE) {
            CloseHandle(handle_);
        }
    }

    bool valid() const noexcept { return handle_ != INVALID_HANDLE_VALUE; }
    bool verified() const noexcept {
        return size_ == expected_size_ && CRC_GET_DIGEST(crc_) == expected_crc_;
    }

    STDMETHOD(QueryInterface)(REFIID iid, void** object) noexcept override {
        if (object == nullptr) {
            return E_POINTER;
        }
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_ISequentialOutStream) {
            *object = static_cast<ISequentialOutStream*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHOD_(ULONG, AddRef)() noexcept override { return ++refs_; }

    STDMETHOD_(ULONG, Release)() noexcept override {
        const ULONG remaining = --refs_;
        if (remaining == 0) {
            delete this;
        }
        return remaining;
    }

    STDMETHOD(Write)(const void* data, UInt32 size, UInt32* processed_size) noexcept override {
        if (processed_size == nullptr) {
            return E_POINTER;
        }
        *processed_size = 0;
        if (is_cancelled(cancellation_flag_)) {
            return E_ABORT;
        }
        if (size_ > expected_size_ || size > expected_size_ - size_) {
            *corrupt_ = true;
            return E_FAIL;
        }
        auto* completed = reinterpret_cast<volatile LONG64*>(&progress_->completed_bytes);
        LONG64 observed = InterlockedCompareExchange64(completed, 0, 0);
        while (true) {
            if (observed < 0) {
                return E_FAIL;
            }
            const std::uint64_t current = static_cast<std::uint64_t>(observed);
            if (current > output_limit_ || size > output_limit_ - current) {
                *corrupt_ = true;
                return E_FAIL;
            }
            const LONG64 desired = observed + static_cast<LONG64>(size);
            const LONG64 prior = InterlockedCompareExchange64(completed, desired, observed);
            if (prior == observed) {
                break;
            }
            observed = prior;
        }
        DWORD written = 0;
        ++metrics_->write_calls;
        metrics_->write_bytes += size;
        const std::uint64_t write_started = performance_counter();
        DWORD write_size = size;
        if (test_write_fault_ == TestWriteFault::failure) {
            CloseHandle(handle_);
            handle_ = INVALID_HANDLE_VALUE;
        } else if (test_write_fault_ == TestWriteFault::short_write && write_size > 0) {
            --write_size;
        }
        const BOOL write_succeeded = WriteFile(handle_, data, write_size, &written, nullptr);
        const std::uint64_t write_finished = performance_counter();
        if (write_finished >= write_started) {
            metrics_->write_ticks += write_finished - write_started;
        }
        if (!write_succeeded) {
            const DWORD write_error = GetLastError();
            *io_failed_ = true;
            InterlockedExchangeAdd64(completed, -static_cast<LONG64>(size));
            return HRESULT_FROM_WIN32(write_error);
        }
        if (written != size) {
            *io_failed_ = true;
            InterlockedExchangeAdd64(completed, -static_cast<LONG64>(size));
            return HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
        }
        size_ += written;
        const std::uint64_t crc_started = performance_counter();
        crc_ = CrcUpdate(crc_, data, written);
        const std::uint64_t crc_finished = performance_counter();
        if (crc_finished >= crc_started) {
            metrics_->crc_ticks += crc_finished - crc_started;
        }
        *processed_size = written;
        return S_OK;
    }

private:
    std::atomic<ULONG> refs_;
    HANDLE handle_;
    std::uint64_t expected_size_;
    std::uint32_t expected_crc_;
    std::uint64_t output_limit_;
    yz_progress_counters* progress_;
    volatile std::uint32_t* cancellation_flag_;
    bool* corrupt_;
    bool* io_failed_;
    yz_zip_metrics* metrics_;
    std::uint64_t size_;
    std::uint32_t crc_;
    TestWriteFault test_write_fault_;
};

class ExtractCallback final : public IArchiveExtractCallback {
public:
    ExtractCallback(
        const yz_zip_job* jobs,
        const std::size_t count,
        const std::uint64_t output_limit,
        yz_progress_counters* progress,
        volatile std::uint32_t* cancellation_flag) noexcept
        : refs_(1),
          jobs_(jobs),
          count_(count),
          output_limit_(output_limit),
          progress_(progress),
          cancellation_flag_(cancellation_flag),
          current_(nullptr),
          operation_failed_(false),
          corrupt_(false),
          io_failed_(false),
          metrics_(&g_last_metrics) {
        lookup_.reserve(count);
        for (std::size_t index = 0; index < count; ++index) {
            lookup_[jobs[index].source_index] = &jobs[index];
        }
    }

    ~ExtractCallback() { release_current(); }

    bool operation_failed() const noexcept { return operation_failed_; }
    bool corrupt() const noexcept { return corrupt_; }
    bool io_failed() const noexcept { return io_failed_; }

    STDMETHOD(QueryInterface)(REFIID iid, void** object) noexcept override {
        if (object == nullptr) {
            return E_POINTER;
        }
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IArchiveExtractCallback) {
            *object = static_cast<IArchiveExtractCallback*>(this);
        } else if (iid == IID_IProgress) {
            *object = static_cast<IProgress*>(this);
        } else {
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    STDMETHOD_(ULONG, AddRef)() noexcept override { return ++refs_; }

    STDMETHOD_(ULONG, Release)() noexcept override {
        const ULONG remaining = --refs_;
        if (remaining == 0) {
            delete this;
        }
        return remaining;
    }

    STDMETHOD(SetTotal)(UInt64 total) noexcept override {
        decode_total_ = total;
        return is_cancelled(cancellation_flag_) ? E_ABORT : S_OK;
    }

    STDMETHOD(SetCompleted)(const UInt64*) noexcept override {
        return is_cancelled(cancellation_flag_) ? E_ABORT : S_OK;
    }

    STDMETHOD(GetStream)(
        UInt32 index,
        ISequentialOutStream** stream,
        Int32 ask_mode) noexcept override {
        ScopedMetric metric(&metrics_->callback_ticks);
        ++metrics_->callback_calls;
        if (stream == nullptr) {
            return E_POINTER;
        }
        *stream = nullptr;
        release_current();
        if (is_cancelled(cancellation_flag_)) {
            return E_ABORT;
        }
        if (ask_mode != NArchive::NExtract::NAskMode::kExtract) {
            return S_OK;
        }
        const yz_zip_job* job = find_job(index);
        if (job == nullptr || job->staging_path == nullptr) {
            operation_failed_ = true;
            return E_FAIL;
        }
        current_ = new (std::nothrow) StagingOutStream(
            *job,
            output_limit_,
            progress_,
            cancellation_flag_,
            &corrupt_,
            &io_failed_,
            metrics_);
        if (current_ == nullptr) {
            return E_OUTOFMEMORY;
        }
        if (!current_->valid()) {
            operation_failed_ = true;
            io_failed_ = true;
            release_current();
            return E_FAIL;
        }
        current_->AddRef();
        *stream = current_;
        return S_OK;
    }

    STDMETHOD(PrepareOperation)(Int32) noexcept override {
        return is_cancelled(cancellation_flag_) ? E_ABORT : S_OK;
    }

    STDMETHOD(SetOperationResult)(Int32 result) noexcept override {
        if (current_ == nullptr) {
            if (result != NArchive::NExtract::NOperationResult::kOK) {
                operation_failed_ = true;
                if (!io_failed_) corrupt_ = true;
            }
            return operation_failed_ ? E_FAIL : S_OK;
        }
        if (result != NArchive::NExtract::NOperationResult::kOK || !current_->verified()) {
            operation_failed_ = true;
            if (!io_failed_) {
                corrupt_ = true;
            }
        }
        release_current();
        return operation_failed_ ? E_FAIL : S_OK;
    }

private:
    const yz_zip_job* find_job(const UInt32 index) const noexcept {
        ++metrics_->lookup_calls;
        const auto found = lookup_.find(index);
        return found == lookup_.end() ? nullptr : found->second;
    }

    void release_current() noexcept {
        if (current_ != nullptr) {
            current_->Release();
            current_ = nullptr;
        }
    }

    std::atomic<ULONG> refs_;
    const yz_zip_job* jobs_;
    std::size_t count_;
    std::uint64_t output_limit_;
    yz_progress_counters* progress_;
    volatile std::uint32_t* cancellation_flag_;
    StagingOutStream* current_;
    bool operation_failed_;
    bool corrupt_;
    bool io_failed_;
    yz_zip_metrics* metrics_;
    std::uint64_t decode_total_ = 0;
    std::unordered_map<UInt32, const yz_zip_job*> lookup_;
};

}  // namespace

std::uint32_t YZ_ACCEL_CALL yz_accel_abi_version() noexcept {
    return YZ_ACCEL_ABI_VERSION;
}

std::uint64_t YZ_ACCEL_CALL yz_accel_capabilities() noexcept {
    return YZ_ACCEL_CAP_ZIP_EXTRACT | YZ_ACCEL_CAP_7Z_EXTRACT | YZ_ACCEL_CAP_ZIP_CREATE;
}

std::size_t YZ_ACCEL_CALL yz_accel_last_error(
    wchar_t* buffer,
    const std::size_t buffer_chars) noexcept {
    try {
        const std::size_t required = g_last_error.size() + 1;
        if (buffer != nullptr && buffer_chars != 0) {
            const std::size_t copied = std::min(g_last_error.size(), buffer_chars - 1);
            g_last_error.copy(buffer, copied);
            buffer[copied] = L'\0';
        }
        return required;
    } catch (...) {
        if (buffer != nullptr && buffer_chars != 0) {
            buffer[0] = L'\0';
        }
        return 1;
    }
}

void YZ_ACCEL_CALL yz_accel_clear_error() noexcept {
    g_last_error.clear();
}

void YZ_ACCEL_CALL yz_accel_cancel(
    volatile std::uint32_t* cancellation_flag) noexcept {
    if (cancellation_flag == nullptr) {
        return;
    }
    auto* target = reinterpret_cast<volatile LONG*>(cancellation_flag);
    InterlockedExchange(target, 1L);
}

static std::uint32_t extract_archive(
    const GUID& class_id,
    const wchar_t* seven_zip_dll,
    const wchar_t* source,
    const yz_zip_job* jobs,
    const std::size_t job_count,
    const std::uint64_t output_limit,
    yz_progress_counters* progress,
    volatile std::uint32_t* cancellation_flag) noexcept {
    g_last_error.clear();
    g_last_metrics = {};
    LARGE_INTEGER qpc_frequency{};
    if (QueryPerformanceFrequency(&qpc_frequency) && qpc_frequency.QuadPart > 0) {
        g_last_metrics.qpc_frequency = static_cast<std::uint64_t>(qpc_frequency.QuadPart);
    }
    if (seven_zip_dll == nullptr || source == nullptr || jobs == nullptr ||
        progress == nullptr || job_count == 0 ||
        job_count > std::numeric_limits<UInt32>::max()) {
        set_error(L"invalid native ZIP extraction arguments");
        return YZ_ACCEL_ERROR;
    }
    if (is_cancelled(cancellation_flag)) {
        return YZ_ACCEL_CANCELLED;
    }
    progress->completed_bytes = 0;
    progress->total_bytes = output_limit;
    HMODULE module = LoadLibraryExW(
        seven_zip_dll,
        nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (module == nullptr) {
        set_error(L"could not load pinned 7z.dll");
        return YZ_ACCEL_UNAVAILABLE;
    }
    std::uint32_t status = YZ_ACCEL_ERROR;
    try {
        auto create_object = reinterpret_cast<CreateObjectFunction>(
            GetProcAddress(module, "CreateObject"));
        if (create_object == nullptr) {
            set_error(L"7z.dll does not export CreateObject");
            status = YZ_ACCEL_UNAVAILABLE;
        } else {
            IInArchive* archive = nullptr;
            HRESULT result = create_object(
                &class_id, &IID_IInArchive, reinterpret_cast<void**>(&archive));
            if (result != S_OK || archive == nullptr) {
                set_error(L"7z.dll could not create the ZIP handler");
                status = YZ_ACCEL_UNAVAILABLE;
            } else {
                InFileStream* input = new (std::nothrow) InFileStream(source);
                if (input == nullptr || !input->valid()) {
                    if (input != nullptr) {
                        input->Release();
                    }
                    set_error(L"could not open the ZIP source");
                } else {
                    result = archive->Open(input, nullptr, nullptr);
                    input->Release();
                    if (result != S_OK) {
                        set_error(L"7z.dll could not open the ZIP archive");
                    } else {
                        std::vector<UInt32> indices;
                        indices.reserve(job_count);
                        for (std::size_t index = 0; index < job_count; ++index) {
                            indices.push_back(jobs[index].source_index);
                        }
                        ExtractCallback* callback = new (std::nothrow) ExtractCallback(
                            jobs, job_count, output_limit, progress, cancellation_flag);
                        if (callback == nullptr) {
                            set_error(L"could not allocate the ZIP extraction callback");
                        } else {
                            result = archive->Extract(
                                indices.data(),
                                static_cast<UInt32>(indices.size()),
                                0,
                                callback);
                            if (is_cancelled(cancellation_flag)) {
                                status = YZ_ACCEL_CANCELLED;
                            } else if (callback->io_failed()) {
                                set_error(L"native ZIP staging I/O failed");
                                status = YZ_ACCEL_IO_ERROR;
                            } else if (callback->corrupt()) {
                                set_error(L"7z.dll reported corrupt ZIP data");
                                status = YZ_ACCEL_CORRUPT;
                            } else if (result != S_OK || callback->operation_failed()) {
                                set_error(L"7z.dll reported a ZIP extraction error");
                            } else {
                                status = YZ_ACCEL_OK;
                            }
                            callback->Release();
                        }
                        const HRESULT close_result = archive->Close();
                        g_last_metrics.close_hresult = static_cast<std::uint32_t>(close_result);
                        if (close_result != S_OK && status == YZ_ACCEL_OK) {
                            set_error(L"7z.dll failed to close the ZIP archive");
                            status = YZ_ACCEL_ERROR;
                        }
                    }
                }
                archive->Release();
            }
        }
    } catch (...) {
        set_error(L"unexpected exception in native ZIP extraction");
        status = YZ_ACCEL_ERROR;
    }
    if (!FreeLibrary(module)) {
        g_last_metrics.free_library_error = GetLastError();
        if (g_last_error.empty()) {
            set_error(L"7z.dll cleanup failed after ZIP extraction");
        }
    }
    return status;
}

std::uint32_t YZ_ACCEL_CALL yz_accel_extract_zip(
    const wchar_t* seven_zip_dll, const wchar_t* source, const yz_zip_job* jobs,
    const std::size_t job_count, const std::uint64_t output_limit,
    yz_progress_counters* progress, volatile std::uint32_t* cancellation_flag) noexcept {
    return extract_archive(kZipClassId, seven_zip_dll, source, jobs, job_count,
                           output_limit, progress, cancellation_flag);
}

std::uint32_t YZ_ACCEL_CALL yz_accel_inspect_7z(
    const wchar_t* seven_zip_dll, const wchar_t* source, yz_archive_info* info) noexcept {
    g_last_error.clear();
    if (seven_zip_dll == nullptr || source == nullptr || info == nullptr) return YZ_ACCEL_ERROR;
    *info = {};
    HMODULE module = LoadLibraryExW(seven_zip_dll, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (module == nullptr) return YZ_ACCEL_UNAVAILABLE;
    std::uint32_t status = YZ_ACCEL_ERROR;
    IInArchive* archive = nullptr;
    InFileStream* input = nullptr;
    try {
      auto create_object = reinterpret_cast<CreateObjectFunction>(GetProcAddress(module, "CreateObject"));
      if (create_object == nullptr || create_object(&k7zClassId, &IID_IInArchive,
            reinterpret_cast<void**>(&archive)) != S_OK || archive == nullptr) {
        status = YZ_ACCEL_UNAVAILABLE;
    } else {
        input = new (std::nothrow) InFileStream(source);
        if (input != nullptr && input->valid() && archive->Open(input, nullptr, nullptr) == S_OK) {
            UInt32 count = 0;
            if (archive->GetNumberOfItems(&count) == S_OK) {
                info->item_count = count;
                bool encrypted = false;
                bool unsupported = false;
                PROPVARIANT archive_method{};
                PropVariantInit(&archive_method);
                if (archive->GetArchiveProperty(kpidMethod, &archive_method) == S_OK &&
                    archive_method.vt == VT_BSTR) {
                    if (!is_plain_lzma_method(archive_method.bstrVal)) {
                        unsupported = true;
                    }
                } else {
                    unsupported = true;
                }
                PropVariantClear(&archive_method);
                PROPVARIANT archive_encrypted{};
                PropVariantInit(&archive_encrypted);
                if (archive->GetArchiveProperty(kpidEncrypted, &archive_encrypted) == S_OK &&
                    archive_encrypted.vt == VT_BOOL && archive_encrypted.boolVal != VARIANT_FALSE) encrypted = true;
                PropVariantClear(&archive_encrypted);
                for (UInt32 i = 0; i < count; ++i) {
                    PROPVARIANT value{};
                    PropVariantInit(&value);
                    if (archive->GetProperty(i, kpidEncrypted, &value) == S_OK &&
                        value.vt == VT_BOOL && value.boolVal != VARIANT_FALSE) encrypted = true;
                    PropVariantClear(&value);
                    PropVariantInit(&value);
                    if (archive->GetProperty(i, kpidSymLink, &value) == S_OK &&
                        ((value.vt == VT_BOOL && value.boolVal != VARIANT_FALSE) ||
                         (value.vt == VT_BSTR && value.bstrVal != nullptr && value.bstrVal[0] != L'\0')))
                        unsupported = true;
                    PropVariantClear(&value);
                    PropVariantInit(&value);
                    if (archive->GetProperty(i, kpidAttrib, &value) == S_OK && value.vt == VT_UI4) {
                        const UInt32 attributes = value.ulVal;
                        const UInt32 unix_mode = attributes >> 16;
                        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ||
                            ((attributes & 0x8000U) != 0 && (unix_mode & 0xF000U) == 0xA000U))
                            unsupported = true;
                    }
                    PropVariantClear(&value);
                    PropVariantInit(&value);
                    if (archive->GetProperty(i, kpidMethod, &value) == S_OK && value.vt == VT_BSTR) {
                        if (!is_plain_lzma_method(value.bstrVal)) {
                            unsupported = true;
                        }
                    }
                    PropVariantClear(&value);
                }
                PROPVARIANT solid{};
                PropVariantInit(&solid);
                if (archive->GetArchiveProperty(kpidSolid, &solid) == S_OK &&
                    solid.vt == VT_BOOL && solid.boolVal != VARIANT_FALSE) info->flags |= YZ_ARCHIVE_SOLID;
                PropVariantClear(&solid);
                if (encrypted) info->flags |= YZ_ARCHIVE_ENCRYPTED;
                if (!encrypted && !unsupported) info->flags |= YZ_ARCHIVE_SUPPORTED;
                status = (info->flags & YZ_ARCHIVE_SUPPORTED) ? YZ_ACCEL_OK : YZ_ACCEL_UNSUPPORTED;
            }
            const HRESULT close_result = archive->Close();
            g_last_metrics.close_hresult = static_cast<std::uint32_t>(close_result);
            if (close_result != S_OK) {
                set_error(L"7z.dll failed to close the 7z archive after inspection");
                status = YZ_ACCEL_ERROR;
            }
        } else {
            status = YZ_ACCEL_CORRUPT;
        }
      }
    } catch (...) {
        status = YZ_ACCEL_ERROR;
    }
    if (input != nullptr) input->Release();
    if (archive != nullptr) archive->Release();
    if (!FreeLibrary(module)) {
        g_last_metrics.free_library_error = GetLastError();
        try {
            set_error(L"7z.dll cleanup failed after 7z inspection");
        } catch (...) {
        }
        status = YZ_ACCEL_ERROR;
    }
    return status;
}

std::uint32_t YZ_ACCEL_CALL yz_accel_extract_7z(
    const wchar_t* seven_zip_dll, const wchar_t* source, const yz_zip_job* jobs,
    const std::size_t job_count, const std::uint64_t output_limit,
    yz_progress_counters* progress, volatile std::uint32_t* cancellation_flag) noexcept {
    yz_archive_info info{};
    const std::uint32_t inspected = yz_accel_inspect_7z(seven_zip_dll, source, &info);
    if (inspected != YZ_ACCEL_OK) return inspected;
    return extract_archive(k7zClassId, seven_zip_dll, source, jobs, job_count,
                           output_limit, progress, cancellation_flag);
}

std::uint32_t YZ_ACCEL_CALL yz_accel_create_zip(
    const wchar_t* seven_zip_dll, const yz_zip_create_item* items,
    const std::size_t item_count, const wchar_t* output_part,
    const std::uint32_t level, const std::uint32_t threads,
    const std::uint64_t output_limit,
    yz_progress_counters* progress, volatile std::uint32_t* cancellation_flag) noexcept {
    g_last_error.clear();
    if (seven_zip_dll == nullptr || items == nullptr || item_count == 0 ||
        item_count > std::numeric_limits<UInt32>::max() || output_part == nullptr ||
        progress == nullptr || level > 9 || threads == 0 || output_limit == 0) {
        set_error(L"invalid native ZIP creation arguments");
        return YZ_ACCEL_ERROR;
    }
    progress->completed_bytes = 0;
    progress->total_bytes = 0;
    for (std::size_t index = 0; index < item_count; ++index) {
        if (items[index].source_path == nullptr || items[index].archive_name == nullptr ||
            items[index].archive_name[0] == L'\0') {
            set_error(L"invalid native ZIP item");
            return YZ_ACCEL_ERROR;
        }
        if (!items[index].is_dir) {
            if (progress->total_bytes > std::numeric_limits<std::uint64_t>::max() - items[index].size) {
                set_error(L"native ZIP input size overflow");
                return YZ_ACCEL_ERROR;
            }
            progress->total_bytes += items[index].size;
        }
    }
    if (is_cancelled(cancellation_flag)) return YZ_ACCEL_CANCELLED;
    HMODULE module = LoadLibraryExW(seven_zip_dll, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (module == nullptr) {
        set_error(L"could not load pinned 7z.dll");
        return YZ_ACCEL_UNAVAILABLE;
    }
    IOutArchive* archive = nullptr;
    ISetProperties* properties = nullptr;
    CreateOutStream* output = nullptr;
    CreateCallback* callback = nullptr;
    std::uint32_t status = YZ_ACCEL_ERROR;
    try {
        auto create_object = reinterpret_cast<CreateObjectFunction>(GetProcAddress(module, "CreateObject"));
        if (create_object == nullptr || create_object(&kZipClassId, &IID_IOutArchive,
                reinterpret_cast<void**>(&archive)) != S_OK || archive == nullptr) {
            set_error(L"7z.dll could not create the ZIP output handler");
            status = YZ_ACCEL_UNAVAILABLE;
        } else if (archive->QueryInterface(IID_ISetProperties,
                       reinterpret_cast<void**>(&properties)) != S_OK || properties == nullptr) {
            set_error(L"ZIP output handler does not support properties");
            status = YZ_ACCEL_UNAVAILABLE;
        } else {
            const wchar_t* names[] = {L"x", L"mt", L"m"};
            PROPVARIANT values[3]{};
            for (auto& value : values) PropVariantInit(&value);
            values[0].vt = VT_UI4; values[0].ulVal = level;
            values[1].vt = VT_UI4; values[1].ulVal = threads;
            // ZipHandlerOut.cpp accepts the numeric ZIP method for property "m":
            // 0 = Store, 8 = Deflate.  This avoids the generic string-method parser.
            values[2].vt = VT_UI4; values[2].ulVal = level == 0 ? 0U : 8U;
            HRESULT result = properties->SetProperties(names, values, 3);
            for (auto& value : values) PropVariantClear(&value);
            if (result != S_OK) {
                set_error(L"7z.dll rejected ZIP level or thread properties");
            } else {
                output = new (std::nothrow) CreateOutStream(output_part, output_limit);
                callback = new (std::nothrow) CreateCallback(
                    items, item_count, progress, cancellation_flag);
                if (output == nullptr || callback == nullptr) {
                    set_error(L"could not allocate native ZIP creation callbacks");
                } else if (!output->valid()) {
                    set_error(L"could not create native ZIP output part");
                    status = YZ_ACCEL_IO_ERROR;
                } else {
                    result = archive->UpdateItems(output, static_cast<UInt32>(item_count), callback);
                    bool changed = false;
                    for (std::size_t index = 0; index < item_count; ++index) {
                        if (!source_matches(items[index])) { changed = true; break; }
                    }
                    if (is_cancelled(cancellation_flag)) status = YZ_ACCEL_CANCELLED;
                    else if (output->io_failed() || callback->io_failed()) {
                        set_error(L"native ZIP creation I/O failed"); status = YZ_ACCEL_IO_ERROR;
                    } else if (changed) {
                        set_error(L"native ZIP input changed during creation"); status = YZ_ACCEL_CORRUPT;
                    } else if (callback->corrupt()) {
                        set_error(L"native ZIP input stream size mismatch"); status = YZ_ACCEL_CORRUPT;
                    } else if (result != S_OK || callback->failed()) {
                        set_error_code(L"7z.dll reported a ZIP creation error", result,
                                       callback->operation_result());
                        status = YZ_ACCEL_ERROR;
                    } else status = YZ_ACCEL_OK;
                }
            }
        }
    } catch (...) {
        set_error(L"unexpected exception in native ZIP creation");
        status = YZ_ACCEL_ERROR;
    }
    if (callback != nullptr) callback->Release();
    if (output != nullptr) output->Release();
    if (properties != nullptr) properties->Release();
    if (archive != nullptr) archive->Release();
    if (status != YZ_ACCEL_OK) DeleteFileW(output_part);
    if (!FreeLibrary(module)) {
        g_last_metrics.free_library_error = GetLastError();
        if (status == YZ_ACCEL_OK) {
            set_error(L"7z.dll cleanup failed after ZIP creation");
            DeleteFileW(output_part);
            status = YZ_ACCEL_ERROR;
        }
    }
    return status;
}

std::uint32_t YZ_ACCEL_CALL yz_accel_last_zip_metrics(
    yz_zip_metrics* metrics) noexcept {
    if (metrics == nullptr) {
        return 0;
    }
    *metrics = g_last_metrics;
    return 1;
}
