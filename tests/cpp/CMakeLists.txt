set(
    _sms_native_test_sources
    protocol_tests.cpp
    store_tests.cpp
    c_api_tests.cpp
    lifecycle_tests.cpp
    diagnostics_tests.cpp)

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
    endif()
    target_compile_features(${_target} PRIVATE cxx_std_20)
    set_target_properties(${_target} PROPERTIES CXX_EXTENSIONS OFF)
    target_include_directories(
        ${_target} PRIVATE "${PROJECT_SOURCE_DIR}/src/cpp/src")
    target_compile_definitions(
        ${_target} PRIVATE SMS_REPOSITORY_ROOT="${PROJECT_SOURCE_DIR}")
    target_link_libraries(
        ${_target} PRIVATE SharedMemoryStore::SharedMemoryStore Threads::Threads)

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

set(_sms_interop_agent_source "${CMAKE_CURRENT_SOURCE_DIR}/interop_agent.cpp")
if(EXISTS "${_sms_interop_agent_source}")
    add_executable(sms_cpp_interop_agent "${_sms_interop_agent_source}")
    target_compile_features(sms_cpp_interop_agent PRIVATE cxx_std_20)
    set_target_properties(sms_cpp_interop_agent PROPERTIES CXX_EXTENSIONS OFF)
    target_link_libraries(
        sms_cpp_interop_agent
        PRIVATE SharedMemoryStore::SharedMemoryStore Threads::Threads)

    if(WIN32)
        add_custom_command(
            TARGET sms_cpp_interop_agent
            POST_BUILD
            COMMAND
                "${CMAKE_COMMAND}" -E copy_if_different
                "$<TARGET_FILE:SharedMemoryStore>"
                "$<TARGET_FILE_DIR:sms_cpp_interop_agent>"
            VERBATIM)
    endif()
endif()
