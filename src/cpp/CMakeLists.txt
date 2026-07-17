set(
    _sms_library_sources
    "${CMAKE_CURRENT_SOURCE_DIR}/src/protocol.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/store.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/c_api.cpp")

if(WIN32)
    set(_sms_platform_source
        "${CMAKE_CURRENT_SOURCE_DIR}/src/platform_windows.cpp")
    set(_sms_platform_definition SMS_PLATFORM_WINDOWS=1)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(_sms_platform_source
        "${CMAKE_CURRENT_SOURCE_DIR}/src/platform_linux.cpp")
    set(_sms_platform_definition SMS_PLATFORM_LINUX=1)
else()
    message(
        FATAL_ERROR
            "SharedMemoryStore currently supports only Windows and Linux; detected '${CMAKE_SYSTEM_NAME}'."
    )
endif()

list(APPEND _sms_library_sources "${_sms_platform_source}")

# Keep the complete SMS2 module topology visible to CMake and IDE generators
# while the test-first port is being delivered incrementally. A planned file is
# not added to an active target until it exists, so setup commits continue to
# configure and build the current native implementation.
set(
    _sms_planned_library_files
    "${CMAKE_CURRENT_SOURCE_DIR}/src/mapped_atomic.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/layout_v2.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/layout_v2.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/control_words.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/operation_budget.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/checkpoint.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/checkpoint.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/lifecycle_gate.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/cold_open.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/cold_open.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/store_control.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/store_control.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/participant_registry.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/participant_registry.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/platform_identity.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/platform_identity.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/key_directory.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/key_directory.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/slot_table.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/slot_table.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/reservation_memory.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/lease_registry.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/lease_registry.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/reclaimer.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/reclaimer.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/recovery.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/recovery.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/diagnostics_v2.hpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/src/diagnostics_v2.cpp")

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    list(
        APPEND
        _sms_planned_library_files
        "${CMAKE_CURRENT_SOURCE_DIR}/src/linux_owner_lifecycle.hpp"
        "${CMAKE_CURRENT_SOURCE_DIR}/src/linux_owner_lifecycle.cpp")
endif()

foreach(_sms_planned_library_file IN LISTS _sms_planned_library_files)
    if(EXISTS "${_sms_planned_library_file}")
        list(APPEND _sms_library_sources "${_sms_planned_library_file}")
    endif()
endforeach()

set(
    _sms_public_headers
    "${CMAKE_CURRENT_SOURCE_DIR}/include/shared_memory_store/c_api.h"
    "${CMAKE_CURRENT_SOURCE_DIR}/include/shared_memory_store/store.hpp")

function(_sms_configure_library target_name)
    target_sources(
        ${target_name}
        PRIVATE ${_sms_library_sources} ${_sms_public_headers})

    target_compile_features(${target_name} PUBLIC cxx_std_20)
    set_target_properties(
        ${target_name}
        PROPERTIES CXX_EXTENSIONS OFF
                   POSITION_INDEPENDENT_CODE ON
                   CXX_VISIBILITY_PRESET hidden
                   VISIBILITY_INLINES_HIDDEN YES
                   LINKER_LANGUAGE CXX)

    target_include_directories(
        ${target_name}
        PUBLIC $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
               $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>
        PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")

    target_compile_definitions(
        ${target_name}
        PRIVATE SMS_BUILDING_LIBRARY=1 ${_sms_platform_definition})

    if(WIN32)
        target_compile_definitions(${target_name} PRIVATE WIN32_LEAN_AND_MEAN
                                                          NOMINMAX)
        if(MINGW)
            # Wheels and native archives must not depend on an unbundled
            # MinGW C++ runtime or unwinder DLL.
            target_link_options(${target_name} PRIVATE -static)
        endif()
    else()
        target_compile_definitions(${target_name} PRIVATE _FILE_OFFSET_BITS=64)
    endif()

    target_link_libraries(${target_name} PRIVATE Threads::Threads)

    if(MSVC)
        target_compile_options(${target_name} PRIVATE /W4 /permissive- /EHsc)
    else()
        target_compile_options(${target_name} PRIVATE -Wall -Wextra -Wpedantic)
    endif()
endfunction()

add_library(SharedMemoryStore SHARED)
add_library(SharedMemoryStore::SharedMemoryStore ALIAS SharedMemoryStore)
_sms_configure_library(SharedMemoryStore)
set_target_properties(
    SharedMemoryStore
    PROPERTIES EXPORT_NAME SharedMemoryStore
               OUTPUT_NAME shared_memory_store
               WINDOWS_EXPORT_ALL_SYMBOLS OFF)
if(WIN32)
    set_target_properties(SharedMemoryStore PROPERTIES PREFIX "")
endif()

# Native installations use the ABI major in the soname. Python wheels need a
# stable, unversioned filename for the standard-library loader.
if(NOT DEFINED SKBUILD AND SMS_PYTHON_INSTALL_DIR STREQUAL "")
    set_target_properties(
        SharedMemoryStore
        PROPERTIES VERSION "${PROJECT_VERSION}"
                   SOVERSION 2)
endif()

if(SMS_BUILD_STATIC)
    add_library(SharedMemoryStoreStatic STATIC)
    add_library(SharedMemoryStore::SharedMemoryStoreStatic ALIAS
                SharedMemoryStoreStatic)
    _sms_configure_library(SharedMemoryStoreStatic)
    target_compile_definitions(SharedMemoryStoreStatic PUBLIC SMS_STATIC=1)
    set_target_properties(
        SharedMemoryStoreStatic
        PROPERTIES EXPORT_NAME SharedMemoryStoreStatic
                   OUTPUT_NAME shared_memory_store_static)
endif()

# Repository checkpoint agents need the real engine graph with instrumentation
# enabled. Keep this target private to SMS_BUILD_TESTS: it is never installed or
# exported and therefore cannot change either packaged native surface.
if(SMS_BUILD_TESTS)
    add_library(SharedMemoryStoreCheckpointRuntime STATIC)
    _sms_configure_library(SharedMemoryStoreCheckpointRuntime)
    target_compile_definitions(
        SharedMemoryStoreCheckpointRuntime
        PUBLIC SMS_STATIC=1 SMS_ENABLE_TEST_CHECKPOINTS=1)
    set_target_properties(
        SharedMemoryStoreCheckpointRuntime
        PROPERTIES OUTPUT_NAME shared_memory_store_checkpoint_runtime)

    # Python repository tests need the same real instrumented engine behind a
    # loadable C ABI plus a private process-local callback bridge. This target
    # is deliberately absent from every install/export rule.
    add_library(SharedMemoryStorePythonCheckpointRuntime SHARED)
    _sms_configure_library(SharedMemoryStorePythonCheckpointRuntime)
    target_sources(
        SharedMemoryStorePythonCheckpointRuntime
        PRIVATE "${PROJECT_SOURCE_DIR}/tests/cpp/python_checkpoint_bridge.cpp")
    target_compile_definitions(
        SharedMemoryStorePythonCheckpointRuntime
        PRIVATE SMS_ENABLE_TEST_CHECKPOINTS=1)
    set_target_properties(
        SharedMemoryStorePythonCheckpointRuntime
        PROPERTIES OUTPUT_NAME shared_memory_store_python_checkpoint)
    if(WIN32)
        set_target_properties(
            SharedMemoryStorePythonCheckpointRuntime PROPERTIES PREFIX "")
    endif()
endif()

if(SMS_INSTALL)
    install(
        TARGETS SharedMemoryStore
        EXPORT SharedMemoryStoreTargets
        RUNTIME DESTINATION "${CMAKE_INSTALL_BINDIR}" COMPONENT Runtime
        LIBRARY DESTINATION "${CMAKE_INSTALL_LIBDIR}" COMPONENT Runtime
                NAMELINK_COMPONENT Development
        ARCHIVE DESTINATION "${CMAKE_INSTALL_LIBDIR}" COMPONENT Development
        INCLUDES DESTINATION "${CMAKE_INSTALL_INCLUDEDIR}")

    if(SMS_BUILD_STATIC)
        install(
            TARGETS SharedMemoryStoreStatic
            EXPORT SharedMemoryStoreTargets
            ARCHIVE DESTINATION "${CMAKE_INSTALL_LIBDIR}"
                    COMPONENT Development
            INCLUDES DESTINATION "${CMAKE_INSTALL_INCLUDEDIR}")
    endif()

    install(
        DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/include/"
        DESTINATION "${CMAKE_INSTALL_INCLUDEDIR}"
        COMPONENT Development
        FILES_MATCHING
        PATTERN "*.h"
        PATTERN "*.hpp")

    if(NOT SMS_PYTHON_INSTALL_DIR STREQUAL "")
        install(
            TARGETS SharedMemoryStore
            RUNTIME DESTINATION "${SMS_PYTHON_INSTALL_DIR}" COMPONENT Python
            LIBRARY DESTINATION "${SMS_PYTHON_INSTALL_DIR}" COMPONENT Python
                    NAMELINK_SKIP)
    endif()
endif()
