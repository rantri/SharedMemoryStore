set(
    _sms_native_test_sources
    protocol_tests.cpp
    store_tests.cpp
    c_api_tests.cpp
    lifecycle_tests.cpp
    diagnostics_tests.cpp)

# Register the planned SMS2 test topology without making an incomplete test
# source part of the generated build. Each source becomes an ordinary CTest
# executable automatically in the first commit that creates it.
set(
    _sms_planned_native_test_sources
    layout_v2_tests.cpp
    control_word_tests.cpp
    atomic_budget_tests.cpp
    mapped_atomic_litmus.cpp
    cold_open_tests.cpp
    participant_registry_tests.cpp
    directory_lookup_tests.cpp
    directory_schedule_tests.cpp
    directory_collision_tests.cpp
    slot_reservation_tests.cpp
    publish_v2_tests.cpp
    lease_v2_tests.cpp
    remove_reclaim_v2_tests.cpp
    recovery_v2_tests.cpp
    disposal_v2_tests.cpp
    lock_free_multiprocess_tests.cpp
    diagnostics_v2_tests.cpp
    interop_agent_protocol_tests.cpp)

if(WIN32)
    list(APPEND _sms_planned_native_test_sources platform_windows_v2_tests.cpp)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    list(APPEND _sms_planned_native_test_sources platform_linux_v2_tests.cpp)
endif()

foreach(_sms_planned_test_source IN LISTS _sms_planned_native_test_sources)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/${_sms_planned_test_source}")
        list(APPEND _sms_native_test_sources "${_sms_planned_test_source}")
    endif()
endforeach()

function(_sms_add_native_test source_name)
    set(_source "${CMAKE_CURRENT_SOURCE_DIR}/${source_name}")
    if(NOT EXISTS "${_source}")
        return()
    endif()

    get_filename_component(_test_stem "${source_name}" NAME_WE)
    set(_target "sms_${_test_stem}")

    add_executable(${_target} "${_source}")
    if(_test_stem STREQUAL "protocol_tests")
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem STREQUAL "layout_v2_tests")
        # LayoutV2 is an internal primitive and is intentionally hidden from
        # the shared-library ABI, so its implementation belongs directly to
        # the white-box conformance target. Its checked arithmetic comes from
        # the canonical protocol utilities, which are hidden for the same
        # reason and therefore compile into this target too.
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/layout_v2.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem STREQUAL "participant_registry_tests")
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/participant_registry.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/layout_v2.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem STREQUAL "cold_open_tests")
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/cold_open.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/store_control.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/participant_registry.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/layout_v2.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem STREQUAL "platform_windows_v2_tests")
        # Exercise the hidden Windows gate/mapping transaction and the complete
        # cold attach while the named gate is retained. These internals do not
        # belong to the installed ABI, so compile their white-box graph here.
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/platform_windows.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/cold_open.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/store_control.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/participant_registry.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/layout_v2.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem MATCHES "^directory_(lookup|schedule|collision)_tests$")
        # KeyDirectory is a hidden mapped-state primitive. Compile it and the
        # canonical layout arithmetic directly into each white-box schedule
        # target so no internal symbol is added to the installed ABI.
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/key_directory.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/layout_v2.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem STREQUAL "platform_linux_v2_tests")
        # Resource-protocol-2 ownership helpers are hidden platform internals.
        # Compile them and canonical resource-name derivation directly into the
        # Linux white-box test while public open/close still exercises the
        # installed shared-library path.
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/linux_owner_lifecycle.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem STREQUAL "slot_reservation_tests")
        # SlotTable is hidden implementation state. Compile the transition
        # module and its layout arithmetic directly into this white-box target.
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/slot_table.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/layout_v2.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem STREQUAL "lease_v2_tests")
        # LeaseRegistry is likewise hidden and intentionally has no store or
        # platform dependency, so its white-box test links only these sources.
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/lease_registry.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/layout_v2.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    elseif(_test_stem STREQUAL "recovery_v2_tests")
        # Recovery composes only hidden SMS2 mapped-state primitives. Compile
        # that white-box graph directly so no internal recovery symbol escapes
        # through the installed shared-library ABI.
        target_sources(
            ${_target}
            PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src/recovery.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/reclaimer.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/key_directory.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/slot_table.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/lease_registry.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/participant_registry.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/layout_v2.cpp"
                    "${PROJECT_SOURCE_DIR}/src/cpp/src/protocol.cpp")
    endif()
    target_compile_features(${_target} PRIVATE cxx_std_20)
    set_target_properties(${_target} PROPERTIES CXX_EXTENSIONS OFF)
    target_include_directories(
        ${_target}
        PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src"
                "${PROJECT_SOURCE_DIR}/src/cpp/include")
    target_compile_definitions(
        ${_target} PRIVATE SMS_REPOSITORY_ROOT="${PROJECT_SOURCE_DIR}")
    if(_test_stem STREQUAL "slot_reservation_tests" OR
       _test_stem STREQUAL "lease_v2_tests" OR
       _test_stem STREQUAL "recovery_v2_tests")
        target_link_libraries(${_target} PRIVATE Threads::Threads)
    else()
        target_link_libraries(
            ${_target} PRIVATE SharedMemoryStore::SharedMemoryStore Threads::Threads)
    endif()

    if(MSVC)
        target_compile_options(${_target} PRIVATE /W4 /permissive- /EHsc)
    else()
        target_compile_options(${_target} PRIVATE -Wall -Wextra -Wpedantic)
    endif()

    if(WIN32)
        add_custom_command(
            TARGET ${_target}
            POST_BUILD
            COMMAND
                "${CMAKE_COMMAND}" -E copy_if_different
                "$<TARGET_FILE:SharedMemoryStore>" "$<TARGET_FILE_DIR:${_target}>"
            VERBATIM)
    endif()

    add_test(NAME "SharedMemoryStore.${_test_stem}" COMMAND ${_target})
    set_tests_properties(
        "SharedMemoryStore.${_test_stem}"
        PROPERTIES WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}")
endfunction()

foreach(_sms_test_source IN LISTS _sms_native_test_sources)
    _sms_add_native_test("${_sms_test_source}")
endforeach()

function(_sms_add_native_agent target_name source_name)
    set(_source "${CMAKE_CURRENT_SOURCE_DIR}/${source_name}")
    if(NOT EXISTS "${_source}")
        return()
    endif()

    add_executable(${target_name} "${_source}")
    target_compile_features(${target_name} PRIVATE cxx_std_20)
    set_target_properties(${target_name} PROPERTIES CXX_EXTENSIONS OFF)
    target_include_directories(
        ${target_name} PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src")
    target_compile_definitions(
        ${target_name} PRIVATE SMS_REPOSITORY_ROOT="${PROJECT_SOURCE_DIR}")
    set(_sms_agent_runtime SharedMemoryStore::SharedMemoryStore)
    if(ARGC GREATER 2)
        set(_sms_agent_runtime "${ARGV2}")
    endif()
    target_link_libraries(
        ${target_name}
        PRIVATE ${_sms_agent_runtime} Threads::Threads)

    if(MSVC)
        target_compile_options(${target_name} PRIVATE /W4 /permissive- /EHsc)
    else()
        target_compile_options(${target_name} PRIVATE -Wall -Wextra -Wpedantic)
    endif()

    if(WIN32 AND _sms_agent_runtime STREQUAL "SharedMemoryStore::SharedMemoryStore")
        add_custom_command(
            TARGET ${target_name}
            POST_BUILD
            COMMAND
                "${CMAKE_COMMAND}" -E copy_if_different
                "$<TARGET_FILE:SharedMemoryStore>"
                "$<TARGET_FILE_DIR:${target_name}>"
            VERBATIM)
    endif()
endfunction()

_sms_add_native_agent(
    sms_cpp_interop_agent
    interop_agent.cpp
    SharedMemoryStoreCheckpointRuntime)
_sms_add_native_agent(sms_native_fault_agent native_fault_agent.cpp)

if(TARGET sms_lock_free_multiprocess_tests AND TARGET sms_native_fault_agent)
    add_dependencies(
        sms_lock_free_multiprocess_tests
        sms_native_fault_agent)
endif()
if(TARGET sms_interop_agent_protocol_tests AND TARGET sms_cpp_interop_agent)
    add_dependencies(
        sms_interop_agent_protocol_tests
        sms_cpp_interop_agent)
endif()
_sms_add_native_agent(sms_mapped_atomic_agent mapped_atomic_agent.cpp)
