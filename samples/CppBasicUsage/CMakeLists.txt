cmake_minimum_required(VERSION 3.20)

if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(SharedMemoryStoreCppBasicUsage LANGUAGES CXX)
    find_package(SharedMemoryStore CONFIG REQUIRED)
endif()

if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/main.cpp")
    add_executable(shared_memory_store_cpp_sample main.cpp)
    target_compile_features(shared_memory_store_cpp_sample PRIVATE cxx_std_20)
    set_target_properties(shared_memory_store_cpp_sample
                          PROPERTIES CXX_EXTENSIONS OFF)
    target_link_libraries(shared_memory_store_cpp_sample
                          PRIVATE SharedMemoryStore::SharedMemoryStore)

    if(WIN32)
        add_custom_command(
            TARGET shared_memory_store_cpp_sample
            POST_BUILD
            COMMAND
                "${CMAKE_COMMAND}" -E copy_if_different
                "$<TARGET_FILE:SharedMemoryStore::SharedMemoryStore>"
                "$<TARGET_FILE_DIR:shared_memory_store_cpp_sample>"
            VERBATIM)
    endif()
endif()
